#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The loopback fixture checkpoint: one fixture attempt played to extraction through the real gate. The server scene runs
    /// -attemptFixture (fixture 03, the server fixture: one hostile, eliminate-hostiles, no terminal, one operative to extract)
    /// and then -net WITHOUT -netQuiet, so the hostile's AI, the friendlies' autonomy and the hit rolls are the normal ones.
    /// The client is the scripted, operator-assisted harness role `attempt`. It sends only the existing requests and reads only
    /// what it was sent, plus the hostile's start and the extraction point the operator reads from the server's log. Nothing
    /// is warped, damaged directly or completed by a call: the server detects the extraction itself.
    /// </summary>
    public class AttemptLoopbackPlayTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.Director.Clear();
            yield return null;
            var fixture = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Backend", "contracts", "attempt-v1", "03-seeded-collect-and-use.json");
            rig.Bootstrap.Begin(new[] { "-attemptFixture", fixture });
            yield return TestWorld.WaitUntil(() => rig.Bootstrap.Session != null || rig.Bootstrap.HasExited, 60f);
            Assert.That(rig.Bootstrap.Session, Is.Not.Null, rig.Bootstrap.ExitReason);
            rig.BeginNet(quiet: false);
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        [UnityTest]
        public IEnumerator A_scripted_client_plays_the_fixture_attempt_to_extraction_through_the_gate()
        {
            var director = rig.Director;
            var session = rig.Bootstrap.Session;
            var start = session.Attempt;

            // The snapshot squad spawned in slot order, and the hostile's AI is running (no -netQuiet).
            Assert.That(director.Friendlies.Select(f => f.GetComponent<UnitIdentity>().OperativeId), Is.EqualTo(start.Snapshot.Select(s => s.OperativeId)));
            Assert.That(director.Hostiles.Count, Is.EqualTo(1));
            Assert.That(director.Hostiles[0].GetComponent<EnemyAI>().enabled, Is.True, "the hostile fights through its own AI");
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Active));

            // The operator aid, as the server logged it: what the operator types into the client.
            var aid = rig.Logs.SingleOrDefault(l => l.Contains("FIXTURE OPERATOR AID"));
            Assert.That(aid, Is.Not.Null, "the fixture attempt logs the operator aid");
            var hostileHint = NetGround.Format(director.Hostiles[0].transform.position);
            var extractionHint = NetGround.Format(director.Current.ExtractionZone.position);
            StringAssert.Contains("hostile1=" + hostileHint, aid);
            StringAssert.Contains("extraction=" + extractionHint, aid);
            Assert.That(NetGround.TryParse(hostileHint, out var hostilePoint), Is.True);
            Assert.That(NetGround.TryParse(extractionHint, out var extractionPoint), Is.True);

            var hostileHealth = director.Hostiles[0].GetComponent<Health>();
            var hostileDamage = 0;
            hostileHealth.Damaged += amount => hostileDamage += amount;
            var acceptedBefore = rig.Server.Host.Accepted;

            var client = rig.AddClient();
            var harness = client.gameObject.AddComponent<NetHarness>();
            harness.Initialize(client);
            harness.Exit = _ => { };
            yield return rig.Connect(NetLoopbackRig.TokenA);
            Assert.That(client.State, Is.EqualTo(NetClientState.Ready), "the development token authenticates: " + client.State + " " + client.Refusal);
            // What the hostile's own AI did, recorded only (hit rolls are real, so it is not asserted).
            var hostileAi = director.Hostiles[0].GetComponent<EnemyAI>();
            var hostileAttacker = director.Hostiles[0].GetComponent<UnitAttacker>();
            int hostileShots = 0, hostileHits = 0, squadShots = 0, squadDamage = 0;
            var engaged = false;
            hostileAttacker.Fired += _ => hostileShots++;
            hostileAttacker.Attacked += _ => hostileHits++;
            foreach (var friendly in director.Friendlies)
            {
                friendly.GetComponent<UnitAttacker>().Fired += _ => squadShots++;
                friendly.GetComponent<Health>().Damaged += amount => squadDamage += amount;
            }
            harness.Begin("attempt", hostilePoint, extractionPoint);
            var runStart = Time.realtimeSinceStartup;
            while (harness.ExitCode < 0 && Time.realtimeSinceStartup - runStart < 240f)
            {
                engaged |= hostileHealth.IsAlive && hostileAi.Target != null;
                yield return null;
            }
            Debug.Log($"LOOPBACK RUN seconds={Time.realtimeSinceStartup - runStart:0.0} hostileEngaged={engaged} hostileShots={hostileShots} hostileHits={hostileHits} " +
                $"squadShots={squadShots} squadDamage={squadDamage} hostileDamage={hostileDamage}");
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
            yield return TestWorld.WaitUntil(() => session.IsFrozen, 10f);

            // The orders went through the gate, and the hostile died of damage the server resolved.
            Assert.That(rig.Server.Host.Accepted - acceptedBefore, Is.GreaterThanOrEqualTo(3), "moves and attacks accepted by the gate");
            Assert.That(hostileHealth.IsAlive, Is.False);
            Assert.That(hostileDamage, Is.GreaterThanOrEqualTo(hostileHealth.Max), "killed by resolved hits");

            // The server detected the extraction itself: a living operative is inside the zone.
            var zone = director.Current.ExtractionZone.position;
            Assert.That(director.Friendlies.Any(f => f.IsAlive && Flat(f.transform.position - zone) <= MissionContent.ZoneRadius), Is.True);

            // Frozen once, Success, a canonical manifest consistent with the session's facts.
            Assert.That(session.IsFrozen, Is.True);
            Assert.That(session.Outcome.Kind, Is.EqualTo(ManifestBuildKind.Normal), string.Join(",", session.Outcome.Diagnostics));
            var built = session.Outcome.Manifest;
            var manifest = built.Manifest;
            Assert.That(manifest.Outcome, Is.EqualTo(ManifestOutcome.Success));
            Assert.That(manifest.Integrity, Is.EqualTo(ManifestIntegrity.Ok), string.Join(",", session.Facts.FailureCodes));
            Assert.That(manifest.AttemptId, Is.EqualTo(start.AttemptId));
            Assert.That(manifest.Objectives.Select(o => (o.ObjectiveId, o.State)),
                Is.EqualTo(new[] { ("eliminate", ObjectiveStateKind.Completed), ("extract", ObjectiveStateKind.Completed) }));
            Assert.That(manifest.OutcomeSequence, Is.EqualTo(session.Sequence.Committed + 1), "the terminal event follows every committed event");
            Assert.That(session.Sequence.Committed, Is.GreaterThanOrEqualTo(4), "at least hits on the hostile, its death and two objective changes");
            var parsed = ManifestCodec.Parse(built.CanonicalBytes);
            Assert.That(parsed.Code, Is.Null, "the manifest parses");
            Assert.That(parsed.CanonicalBytes, Is.EqualTo(built.CanonicalBytes), "the bytes are canonical");
            Assert.That(parsed.ManifestHash, Is.EqualTo(built.ManifestHash));
            Assert.That(rig.Logs.Any(l => l.Contains("ATTEMPT FROZEN outcome=Success") && l.Contains("outcomeSequence=" + manifest.OutcomeSequence)
                && l.Contains("manifestHash=" + built.ManifestHash)), Is.True, "the frozen line names the outcome, sequence and hash");

            // Each operative still holds exactly its snapshot: original equipment, nothing collected, nothing consumed.
            foreach (var snapshot in start.Snapshot)
            {
                var operative = manifest.Operatives.Single(o => o.Slot == snapshot.Slot);
                var facts = session.Facts.Operatives.Single(o => o.Slot == snapshot.Slot);
                Assert.That(facts.OperativeId, Is.EqualTo(snapshot.OperativeId));
                Assert.That(operative.Alive, Is.EqualTo(director.Friendlies[snapshot.Slot].IsAlive));
                Assert.That(operative.HeldItems.OrderBy(i => i), Is.EqualTo(snapshot.Items.Select(i => Id(i.ItemId)).OrderBy(i => i)), snapshot.OperativeId + " items");
                Assert.That(operative.HeldStacks.Select(s => (s.DefinitionId, s.Quantity)).OrderBy(s => s.DefinitionId),
                    Is.EqualTo(snapshot.Stacks.Select(s => (s.DefinitionId, s.Quantity)).OrderBy(s => s.DefinitionId)), snapshot.OperativeId + " stacks");
                foreach (var slot in new[] { EquipSlot.Weapon, EquipSlot.Armor, EquipSlot.Utility })
                {
                    var equipped = operative.Equipment.SingleOrDefault(e => e.Slot == slot);
                    var expected = snapshot.EquippedIn(slot);
                    Assert.That(equipped != null ? equipped.ItemId : (long?)null, Is.EqualTo(expected != null ? Id(expected) : (long?)null), snapshot.OperativeId + " " + slot);
                }
                Assert.That(facts.Collected, Is.Empty);
                Assert.That(facts.ConsumedOwned, Is.Empty);
            }

            // Nothing was committed locally: no XP, no settlement (there is no backend and no outbox in this run).
            Assert.That(rig.Bootstrap.Inventory.Core.Working, Is.Not.Null, "the server never settles");
            var roster = Object.FindFirstObjectByType<SquadRoster>(FindObjectsInactive.Include);
            Assert.That(roster.SnapshotMode, Is.True);
            Assert.That(roster.Members.Select(m => m.State.Experience), Is.EqualTo(start.Experience), "the backend awards XP, not the server");

            // Nothing after the freeze changes the result.
            var bytes = built.CanonicalBytes;
            var committed = session.Sequence.Committed;
            yield return null;
            Assert.That(session.Outcome.Manifest.CanonicalBytes, Is.EqualTo(bytes));
            Assert.That(session.Sequence.Committed, Is.EqualTo(committed));
        }

        static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        static long Id(string base58) => Base58Id.TryParse(base58, out var id) ? id : -1;
    }
}
#endif
