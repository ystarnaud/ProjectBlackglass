using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class MissionIntelligenceTests
    {
        // Same two-room layout as RegionMapTests: rooms 0 and 1, corridor 2. Tile (x, y) is world (x - 10, y - 5).
        static MissionLayout TwoRooms()
        {
            const int width = 20;
            const int height = 10;
            var floor = new bool[width * height];
            var a = new RectInt(1, 1, 6, 6);
            var b = new RectInt(13, 1, 6, 6);
            var strip = new RectInt(7, 2, 6, 3);
            foreach (var rect in new[] { a, b, strip })
                for (var y = rect.yMin; y < rect.yMax; y++)
                    for (var x = rect.xMin; x < rect.xMax; x++)
                        floor[y * width + x] = true;
            return new MissionLayout(1, 1, width, height, floor)
            {
                Rooms = new[] { new MissionRoom(0, new Vector2Int(0, 0), a), new MissionRoom(1, new Vector2Int(1, 0), b) },
                Connections = new[] { new MissionConnection(0, 1, strip) },
                FriendlyRoom = 0,
            };
        }

        readonly List<GameObject> created = new List<GameObject>();
        MissionLayout layout;
        RegionMap map;
        MissionIntelligence intel;

        [SetUp]
        public void SetUp()
        {
            layout = TwoRooms();
            map = new RegionMap(layout);
            intel = new MissionIntelligence(map, deviceCount: 2);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                Object.DestroyImmediate(go);
            created.Clear();
        }

        Health Enemy(Vector3 at)
        {
            var go = new GameObject("Enemy");
            go.transform.position = at;
            created.Add(go);
            var health = go.AddComponent<Health>();
            health.Initialize(50);
            return health;
        }

        [Test]
        public void EverythingStartsUnknown()
        {
            Assert.That(intel.IsOpen, Is.False);
            for (var r = 0; r < map.Count; r++)
                Assert.That(intel.StateOfRegion(r), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.StateOfDevice(0), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.StateOfPoint(layout.TileCenter(new Vector2Int(3, 3))), Is.EqualTo(KnowledgeState.Unknown));
        }

        [Test]
        public void RevealRegion_IsPermanent_AndReportsWhetherItChangedAnything()
        {
            Assert.That(intel.RevealRegion(1), Is.True);
            Assert.That(intel.RevealRegion(1), Is.False);
            Assert.That(intel.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered));
            intel.BeginPass();
            intel.EndPass();
            Assert.That(intel.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "a pass that does not see it leaves it discovered");
        }

        [Test]
        public void ARegionSeenInAPass_IsObservedThenFallsBackToDiscovered()
        {
            intel.BeginPass();
            intel.MarkRegionObserved(0);
            intel.EndPass();
            Assert.That(intel.StateOfRegion(0), Is.EqualTo(KnowledgeState.Observed));
            intel.BeginPass();
            intel.EndPass();
            Assert.That(intel.StateOfRegion(0), Is.EqualTo(KnowledgeState.Discovered), "discovery persists, live sight does not");
        }

        [Test]
        public void StateOfPoint_UsesTheRegionOfThePoint_AndIsUnknownOutsideEveryRegion()
        {
            intel.RevealRegion(0);
            Assert.That(intel.StateOfPoint(layout.TileCenter(new Vector2Int(3, 3))), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(intel.StateOfPoint(layout.TileCenter(new Vector2Int(16, 3))), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.StateOfPoint(new Vector3(500f, 0f, 500f)), Is.EqualTo(KnowledgeState.Unknown));
        }

        [Test]
        public void RevealArea_DiscoversEveryRegionTheCircleTouches_AndCountsTheNewOnes()
        {
            intel.RevealRegion(0);
            var newly = intel.RevealArea(layout.TileCenter(new Vector2Int(9, 3)), 1f);
            Assert.That(newly, Is.EqualTo(1), "only the corridor is new");
            Assert.That(intel.StateOfRegion(2), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(intel.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown));
        }

        [Test]
        public void ObservedEnemy_IsLiveThenLastKnownAtItsLastPosition_AndStopsTrackingItsHiddenMovement()
        {
            var enemy = Enemy(new Vector3(5f, 1f, 0f));
            intel.TrackEnemy(enemy);
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.IsObserved(enemy), Is.False);

            intel.BeginPass();
            intel.ObserveEnemy(enemy, enemy.transform.position);
            intel.EndPass();
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(intel.IsObserved(enemy), Is.True);

            enemy.transform.position = new Vector3(8f, 1f, 3f);   // moves while unseen
            intel.BeginPass();
            intel.EndPass();
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(intel.IsObserved(enemy), Is.False, "a last-known marker is not a live target");
            Assert.That(intel.TryGetLastKnown(enemy, out var at), Is.True);
            Assert.That(at, Is.EqualTo(new Vector3(5f, 1f, 0f)), "the marker stays where it was last seen");

            intel.BeginPass();
            intel.ObserveEnemy(enemy, enemy.transform.position);
            intel.EndPass();
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(intel.TryGetLastKnown(enemy, out at), Is.True);
            Assert.That(at, Is.EqualTo(new Vector3(8f, 1f, 3f)));
        }

        [Test]
        public void AnEnemyNeverSeen_HasNoLastKnownPosition()
        {
            var enemy = Enemy(Vector3.zero);
            intel.TrackEnemy(enemy);
            Assert.That(intel.TryGetLastKnown(enemy, out _), Is.False);
        }

        [Test]
        public void MarkLastKnown_ModelsABriefingMarkerWithoutSeeingTheEnemy()
        {
            var enemy = Enemy(new Vector3(2f, 1f, 2f));
            intel.TrackEnemy(enemy);
            intel.MarkLastKnown(enemy, new Vector3(2f, 1f, 2f));
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(intel.IsObserved(enemy), Is.False);
            Assert.That(intel.TryGetLastKnown(enemy, out var at), Is.True);
            Assert.That(at, Is.EqualTo(new Vector3(2f, 1f, 2f)));
        }

        [Test]
        public void ADeadEnemy_IsUnknownAndNotObserved_EvenWhileSeenInThePass()
        {
            var enemy = Enemy(Vector3.zero);
            intel.TrackEnemy(enemy);
            intel.BeginPass();
            intel.ObserveEnemy(enemy, Vector3.zero);
            intel.EndPass();
            enemy.TakeDamage(1000);
            Assert.That(intel.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.IsObserved(enemy), Is.False);
            Assert.That(intel.TryGetLastKnown(enemy, out _), Is.False);
            intel.BeginPass();
            Assert.DoesNotThrow(() => intel.EndPass());
        }

        [Test]
        public void ADestroyedEnemy_DoesNotThrowInAPass()
        {
            var enemy = Enemy(Vector3.zero);
            intel.TrackEnemy(enemy);
            intel.BeginPass();
            intel.ObserveEnemy(enemy, Vector3.zero);
            intel.EndPass();
            Object.DestroyImmediate(enemy.gameObject);
            Assert.DoesNotThrow(() =>
            {
                intel.BeginPass();
                intel.EndPass();
            });
        }

        [Test]
        public void AnUntrackedUnit_CountsAsObserved_SoFriendliesAreNeverGated()
        {
            var friendly = Enemy(Vector3.zero);
            Assert.That(intel.IsObserved(friendly), Is.True);
            Assert.That(intel.StateOfEnemy(friendly), Is.EqualTo(KnowledgeState.Unknown), "but it has no enemy intel");
        }

        [Test]
        public void AnOpenModel_KnowsEverything_AndIgnoresPasses()
        {
            var open = new MissionIntelligence(map, 1, open: true);
            var enemy = Enemy(Vector3.zero);
            open.TrackEnemy(enemy);
            Assert.That(open.IsOpen, Is.True);
            Assert.That(open.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(open.StateOfPoint(layout.TileCenter(new Vector2Int(16, 3))), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(open.StateOfEnemy(enemy), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(open.IsObserved(enemy), Is.True);
            Assert.That(open.StateOfDevice(0), Is.EqualTo(KnowledgeState.Discovered));
        }

        [Test]
        public void Devices_AreDiscoveredOnlyByRevealDevice()
        {
            Assert.That(intel.DeviceCount, Is.EqualTo(2));
            Assert.That(intel.RevealDevice(1), Is.True);
            Assert.That(intel.RevealDevice(1), Is.False);
            Assert.That(intel.StateOfDevice(1), Is.EqualTo(KnowledgeState.Discovered));
            Assert.That(intel.StateOfDevice(0), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(intel.StateOfDevice(7), Is.EqualTo(KnowledgeState.Unknown), "an unknown id is unknown, not an exception");
            Assert.That(intel.RevealDevice(7), Is.False);
        }

        [Test]
        public void Changed_FiresOnRealChangesOnly()
        {
            var count = 0;
            intel.Changed += () => count++;
            intel.RevealRegion(0);
            Assert.That(count, Is.EqualTo(1));
            intel.RevealRegion(0);
            Assert.That(count, Is.EqualTo(1));
            intel.BeginPass();
            intel.EndPass();
            Assert.That(count, Is.EqualTo(1), "a pass that changes nothing is silent");
            intel.BeginPass();
            intel.MarkRegionObserved(0);
            intel.EndPass();
            Assert.That(count, Is.EqualTo(2));
        }

        // ---- objectives ----

        sealed class StubObjective : MissionObjective
        {
            readonly bool hasTarget;
            readonly Vector3 position;

            public StubObjective(string id, ObjectiveType type, bool hasTarget, Vector3 position)
                : base(id, type, id, true)
            {
                this.hasTarget = hasTarget;
                this.position = position;
            }

            public override bool HasTarget => hasTarget;
            public override Vector3 TargetPosition => position;
            public override string Describe() => Title;
        }

        List<MissionObjective> Goals() => new List<MissionObjective>
        {
            new StubObjective("eliminate", ObjectiveType.EliminateHostiles, false, Vector3.zero),
            new StubObjective("hack", ObjectiveType.Interact, true, new Vector3(6.5f, 0f, -1.5f)),     // room 1
            new StubObjective("extract", ObjectiveType.ReachZone, true, new Vector3(-6.5f, 0f, -1.5f)),  // room 0
        };

        [TestCase(ObjectiveKnowledge.All, true, true, true)]
        [TestCase(ObjectiveKnowledge.ExtractionAndTerminal, false, true, true)]
        [TestCase(ObjectiveKnowledge.ExtractionOnly, false, false, true)]
        [TestCase(ObjectiveKnowledge.None, false, false, false)]
        public void BindObjectives_SetsTheStartingKnowledge(ObjectiveKnowledge knowledge, bool eliminate, bool hack, bool extract)
        {
            var goals = Goals();
            intel.BindObjectives(goals, knowledge);
            Assert.That(goals[0].IsKnown, Is.EqualTo(eliminate));
            Assert.That(goals[1].IsKnown, Is.EqualTo(hack));
            Assert.That(goals[2].IsKnown, Is.EqualTo(extract));
        }

        [Test]
        public void AnObjectiveWithATarget_BecomesKnownWhenItsRegionIsObserved()
        {
            var goals = Goals();
            intel.BindObjectives(goals, ObjectiveKnowledge.None);
            Assert.That(goals[1].IsKnown, Is.False);
            intel.RevealRegion(1);
            Assert.That(goals[1].IsKnown, Is.False, "a discovered (mapped) region does not teach the objective");
            intel.BeginPass();
            intel.MarkRegionObserved(1);
            intel.EndPass();
            Assert.That(goals[1].IsKnown, Is.True);
            Assert.That(goals[2].IsKnown, Is.False, "room 0 is still unknown");
        }

        [Test]
        public void TheEliminateObjective_BecomesKnownWhenAnyEnemyIsFirstObserved()
        {
            var goals = Goals();
            intel.BindObjectives(goals, ObjectiveKnowledge.None);
            var enemy = Enemy(Vector3.zero);
            intel.TrackEnemy(enemy);
            Assert.That(goals[0].IsKnown, Is.False);
            intel.BeginPass();
            intel.ObserveEnemy(enemy, Vector3.zero);
            intel.EndPass();
            Assert.That(goals[0].IsKnown, Is.True);
            Assert.That(intel.AnyEnemyEverObserved, Is.True);
        }

        [Test]
        public void RevealObjective_MakesItKnown()
        {
            var goals = Goals();
            intel.BindObjectives(goals, ObjectiveKnowledge.None);
            intel.RevealObjective(goals[1]);
            Assert.That(goals[1].IsKnown, Is.True);
        }

        [Test]
        public void AnOpenModel_LeavesObjectivesAlone()
        {
            var open = new MissionIntelligence(map, 0, open: true);
            var goals = Goals();
            open.BindObjectives(goals, ObjectiveKnowledge.None);
            Assert.That(goals[1].IsKnown, Is.True);
        }

        [Test]
        public void BindObjectives_HidesTheEliminateCounts()
        {
            var group = new List<Health> { Enemy(Vector3.zero), Enemy(Vector3.one) };
            var eliminate = new EliminateHostilesObjective("e", "Eliminate", group);
            intel.BindObjectives(new List<MissionObjective> { eliminate }, ObjectiveKnowledge.All);
            Assert.That(eliminate.ShowCounts, Is.False);
            var open = new MissionIntelligence(map, 0, open: true);
            var second = new EliminateHostilesObjective("e2", "Eliminate", group);
            open.BindObjectives(new List<MissionObjective> { second }, ObjectiveKnowledge.All);
            Assert.That(second.ShowCounts, Is.True);
        }
    }
}
