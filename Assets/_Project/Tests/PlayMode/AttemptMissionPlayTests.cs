#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 Tasks 4 and 6 in a server-like world: generation for a validated attempt (its id, its seed, the one-entry-per-roll
    /// fill), the ready report from the filled containers, and a forced bad roll failing generation before any report exists.
    /// </summary>
    public class AttemptMissionPlayTests
    {
        const int Seed = ServerFixture.Seed;
        AttemptWorld world;

        [SetUp]
        public void SetUp() => world = new AttemptWorld();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            world.Dispose();
            yield return null;
        }

        IEnumerator Generate(AttemptStart attempt)
        {
            Assert.That(world.Director.GenerateForAttempt(attempt), Is.True);
            yield return TestWorld.WaitUntil(() => world.Director.State == MissionState.Ready || world.Director.State == MissionState.Failed, 60f);
        }

        [UnityTest]
        public IEnumerator An_attempt_generates_with_its_id_and_seed_and_one_entry_per_roll()
        {
            var attempt = world.Start(Seed);
            yield return Generate(attempt);
            var director = world.Director;
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", director.Report.Failures));
            Assert.That(director.InstanceId, Is.EqualTo(AttemptWorld.AttemptId), "the backend's attempt id, not a fresh one");
            Assert.That(director.Report.Seed, Is.EqualTo(Seed));
            Assert.That(director.Attempt, Is.SameAs(attempt));
            Assert.That(world.Inventory.Core.ActiveMissionId, Is.EqualTo(AttemptWorld.AttemptId));
            var containers = director.AttemptContainers;
            Assert.That(containers.Count, Is.EqualTo(director.Current.Loot.Placed));
            Assert.That(containers.Count, Is.GreaterThan(0));
            for (var i = 0; i < containers.Count; i++)
            {
                Assert.That(containers[i].Index, Is.EqualTo(i));
                var rolls = director.Current.Loot.Containers[i].Items;
                Assert.That(containers[i].EntriesInRollOrder.Select(e => (e.DefinitionId, e.Quantity)), Is.EqualTo(rolls.Select(r => (r.DefinitionId, r.Quantity))));
                Assert.That(director.Current.LootContainers[i].AttemptMode, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator The_ready_report_of_the_fixture_attempt_is_pinned()
        {
            var attempt = world.Start(Seed);
            yield return Generate(attempt);
            Assert.That(world.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", world.Director.Report.Failures));
            var report = AttemptReady.Build(attempt, world.Director, AttemptReady.Pool(world.Director));
            Assert.That(report, Is.Not.Null);
            Assert.That(JsonUtility.ToJson(report), Is.EqualTo(PinnedReady));
        }

        // Generated once from the server fixture (seed 1, two operatives, the shipped loot table), then pinned; the backend's
        // own ready path judges the attempt-v1 fixtures (Task 7).
        const string PinnedReady =
            "{\"processSessionId\":\"0192f1a5-0000-7000-8000-000000000001\",\"layoutHash\":\"cf2e14dece9cc2a6\",\"lootTableVersion\":1,\"lootHash\":\"sha256:86fe2c3850ee11fbf263d93e94873577bf0d0e557148939ef30ab748ca3795c2\",\"objectives\":[{\"objectiveId\":\"eliminate\",\"required\":true},{\"objectiveId\":\"extract\",\"required\":false}],\"lootPool\":[{\"containerIndex\":0,\"line\":0,\"definitionId\":\"item.medkit\",\"quantity\":2},{\"containerIndex\":1,\"line\":0,\"definitionId\":\"item.medkit\",\"quantity\":1},{\"containerIndex\":1,\"line\":1,\"definitionId\":\"item.medkit\",\"quantity\":2},{\"containerIndex\":2,\"line\":0,\"definitionId\":\"item.servo-boots\",\"quantity\":1},{\"containerIndex\":2,\"line\":1,\"definitionId\":\"item.servo-boots\",\"quantity\":1}]}";

        static void SearchNextTo(LootContainer container, CommandableUnit unit)
        {
            container.transform.position = unit.transform.position + Vector3.right;
            Physics.SyncTransforms();
            Assert.That(container.Interactable.TryBegin(unit), Is.True);
            container.Interactable.Advance(unit, 10f);
            Assert.That(container.IsSearched, Is.True);
        }

        [UnityTest]
        public IEnumerator Real_units_feed_the_ledger_and_the_first_end_freezes_the_outcome()
        {
            var attempt = world.Start(Seed);
            world.UseSnapshot(attempt);
            yield return Generate(attempt);
            var director = world.Director;
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", director.Report.Failures));
            var session = world.Session(attempt);
            Assert.That(JsonUtility.ToJson(session.ReadyReport), Is.EqualTo(PinnedReady), "the snapshot squad generates the same fixture mission");
            var c = director.Friendlies[0];
            var d = director.Friendlies[1];
            Assert.That(c.GetComponent<UnitItems>().OperativeId, Is.EqualTo("C"));
            Assert.That(d.GetComponent<UnitItems>().OperativeId, Is.EqualTo("D"));

            // C takes both medkit lines of container 1 (x1 and x2) in one collect, then uses one medkit.
            var twoLines = director.Current.LootContainers[1];
            SearchNextTo(twoLines, c);
            var items = c.GetComponent<UnitItems>();
            Assert.That(items.TryCollect(twoLines, null).Taken, Is.EqualTo(3));
            c.GetComponent<Health>().TakeDamage(50);
            var kit = world.Inventory.WorkingLoadout("C").Bag.Entries.First(e => e.DefinitionId == "item.medkit").InstanceId;
            Assert.That(items.TryUse(kit), Is.True);

            // The end: the hostile eliminated, C at the extraction point.
            foreach (var hostile in director.Hostiles)
                hostile.GetComponent<Health>().TakeDamage(100000);
            c.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(director.Current.ExtractionZone.position);
            yield return TestWorld.WaitUntil(() => session.IsFrozen, 30f);
            Assert.That(session.IsFrozen, Is.True, "the mission ended");
            Assert.That(session.Outcome.Kind, Is.EqualTo(ManifestBuildKind.Normal), string.Join(",", session.Outcome.Diagnostics));
            var manifest = session.Outcome.Manifest.Manifest;
            Assert.That(manifest.Outcome, Is.EqualTo(ManifestOutcome.Success));
            Assert.That(manifest.Integrity, Is.EqualTo(ManifestIntegrity.Ok), string.Join(",", session.Facts.FailureCodes));
            Assert.That(manifest.AttemptId, Is.EqualTo(AttemptWorld.AttemptId));
            Assert.That(manifest.Objectives.Select(o => (o.ObjectiveId, o.State)),
                Is.EqualTo(new[] { ("eliminate", ObjectiveStateKind.Completed), ("extract", ObjectiveStateKind.Completed) }));
            var factsC = session.Facts.Operatives.Single(o => o.OperativeId == "C");
            Assert.That(factsC.Collected.Select(x => (x.ContainerIndex, x.Line, x.Quantity)), Is.EqualTo(new[] { (1, 0, 1), (1, 1, 2) }));
            Assert.That(factsC.ConsumedLoot.Select(x => (x.ContainerIndex, x.Line, x.Quantity)), Is.EqualTo(new[] { (1, 0, 1) }), "loot first, oldest first");
            Assert.That(manifest.Operatives.All(o => o.Alive), Is.True);
            var bytes = session.Outcome.Manifest.CanonicalBytes;

            // After the end: D takes container 0; nothing about the outcome changes, nobody gains XP, nothing settles.
            var other = director.Current.LootContainers[0];
            SearchNextTo(other, d);
            Assert.That(d.GetComponent<UnitItems>().TryCollect(other, null).Taken, Is.GreaterThan(0));
            yield return null;
            Assert.That(session.Outcome.Manifest.CanonicalBytes, Is.EqualTo(bytes));
            Assert.That(world.Roster.Members.All(m => m.State.Experience == 0), Is.True, "the backend awards XP, not the server");
            Assert.That(world.Inventory.Core.Working, Is.Not.Null, "the server never settles");
        }

        [UnityTest]
        public IEnumerator A_roll_over_max_stack_fails_generation_with_its_code_and_no_report()
        {
            var medkit = world.Catalogue.Find("item.medkit");
            var table = LootTable.Create(1, 3, 1, 1, new[] { new LootEntry { item = medkit, minQuantity = 4, maxQuantity = 4, weight = 1 } });
            world.Track(table);
            world.Director.SetLootTable(table);
            LogAssert.Expect(LogType.Error, new Regex("^Mission generation failed"));
            var attempt = world.Start(Seed);
            yield return Generate(attempt);
            Assert.That(world.Director.State, Is.EqualTo(MissionState.Failed));
            StringAssert.Contains("attempt: loot: loot_roll_exceeds_stack container 0 roll 0", world.Director.Report.Failure);
            Assert.That(world.Director.Report.AttemptsMade, Is.EqualTo(1), "a bad roll stops generation: no other attempt rerolls it");
            Assert.That(world.Director.AttemptContainers, Is.Empty);
            Assert.That(AttemptReady.Build(attempt, world.Director, AttemptReady.Pool(world.Director)), Is.Null, "no ready report exists");
        }

        [UnityTest]
        public IEnumerator An_exception_after_the_fill_stops_generation_instead_of_rerolling()
        {
            // The director throws after the containers are filled; another layout attempt would roll new loot.
            world.Dispose();
            world = new AttemptWorld(breakAfterFill: true);
            var attempt = world.Start(Seed);
            world.UseSnapshot(attempt);
            LogAssert.Expect(LogType.Error, new Regex("^Mission generation failed"));
            yield return Generate(attempt);
            Assert.That(world.Director.State, Is.EqualTo(MissionState.Failed));
            StringAssert.Contains("exception", world.Director.Report.Failure);
            Assert.That(world.Director.Report.AttemptsMade, Is.EqualTo(1), "an attempt never retries after its loot was rolled and filled");
        }

        [UnityTest]
        public IEnumerator A_snapshot_operative_without_a_unit_is_refused_by_the_session()
        {
            var attempt = world.Start(Seed);
            world.UseSnapshot(attempt);
            yield return Generate(attempt);
            Assert.That(world.Director.State, Is.EqualTo(MissionState.Ready));
            Object.DestroyImmediate(world.Director.Friendlies[1].GetComponent<UnitItems>());
            var session = AttemptSession.Begin(attempt, world.Director, world.Inventory, ItemLedgerMapping.Catalogue(world.Catalogue), new ServerClock(), "editor", out var refusal);
            Assert.That(session, Is.Null);
            Assert.That(refusal, Is.EqualTo("squad_not_spawned"));
        }

        [UnityTest]
        public IEnumerator An_attempt_without_a_loot_table_fails_closed()
        {
            world.Director.SetLootTable(null);
            LogAssert.Expect(LogType.Error, new Regex("^Mission generation failed"));
            yield return Generate(world.Start(Seed));
            Assert.That(world.Director.State, Is.EqualTo(MissionState.Failed));
            StringAssert.Contains("attempt: loot: no valid loot table", world.Director.Report.Failure);
        }

        [UnityTest]
        public IEnumerator A_plain_generation_after_an_attempt_is_offline_again()
        {
            yield return Generate(world.Start(Seed));
            Assert.That(world.Director.Generate(Seed), Is.True);
            yield return TestWorld.WaitUntil(() => world.Director.State == MissionState.Ready || world.Director.State == MissionState.Failed, 60f);
            Assert.That(world.Director.Attempt, Is.Null);
            Assert.That(world.Director.InstanceId, Is.Not.EqualTo(AttemptWorld.AttemptId));
            Assert.That(world.Director.AttemptContainers, Is.Empty);
            Assert.That(world.Director.Current.LootContainers.All(c => !c.AttemptMode), Is.True);
        }
    }
}
#endif
