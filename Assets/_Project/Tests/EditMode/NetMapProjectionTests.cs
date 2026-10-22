using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The server's whitelist projection of the public map and the known objectives (decision 052).</summary>
    public class NetMapProjectionTests
    {
        static MissionLayout FixtureLayout()
        {
            Assert.That(MissionGenerator.TryAttempt(ServerFixture.Settings().Validated(), 1, out var layout, out var reason), Is.True, reason);
            return layout;
        }

        static IntelligenceSettings Fixture() => ServerFixture.Settings().intelligence;

        [Test]
        public void TheFixtureMission_IsProjected_WithItsFloorsBoxesAndContainersCopiedExactly()
        {
            var layout = FixtureLayout();
            var tiles = new[] { layout.FriendlySpawns[0] };   // any floor tile stands in for a container here
            var map = NetMapProjection.Map(layout, tiles, Fixture(), false, false, out var refusal);
            Assert.That(refusal, Is.Null);
            Assert.That(map.Width, Is.EqualTo(layout.Width));
            Assert.That(map.Height, Is.EqualTo(layout.Height));
            Assert.That(map.Floors, Is.EqualTo(layout.FloorRects));
            Assert.That(map.Boxes.Select(b => (b.Kind, b.Footprint, b.Rise)), Is.EqualTo(layout.Boxes.Select(b => (b.Kind, b.Footprint, b.Height))));
            Assert.That(map.Containers, Is.EqualTo(tiles));
        }

        [TestCase(MapKnowledge.Partial)] [TestCase(MapKnowledge.None)]
        public void AnyMapKnowledgeButFull_IsNotProjected(MapKnowledge knowledge)
        {
            var settings = Fixture();
            settings.map = knowledge;
            Assert.That(NetMapProjection.Map(FixtureLayout(), Array.Empty<Vector2Int>(), settings, false, false, out var refusal), Is.Null);
            StringAssert.Contains("map knowledge", refusal);
        }

        [TestCase(ObjectiveKnowledge.ExtractionAndTerminal)] [TestCase(ObjectiveKnowledge.ExtractionOnly)] [TestCase(ObjectiveKnowledge.None)]
        public void AnyObjectiveKnowledgeButAll_ProjectsNeitherMapNorObjectives(ObjectiveKnowledge knowledge)
        {
            var settings = Fixture();
            settings.objectives = knowledge;
            Assert.That(NetMapProjection.Map(FixtureLayout(), Array.Empty<Vector2Int>(), settings, false, false, out var refusal), Is.Null);
            StringAssert.Contains("objective knowledge", refusal);
            Assert.That(NetMapProjection.Objectives(Goals(out _, out _), settings, false, false), Is.Empty);
        }

        [Test]
        public void FogOff_IsNotProjected()
        {
            var settings = Fixture();
            settings.fogEnabled = false;
            StringAssert.Contains("fog", NetMapProjection.Problem(settings, false, false));
        }

        [Test]
        public void ATerminalOrCameras_AreNotProjectedInThisSlice()
        {
            StringAssert.Contains("terminal", NetMapProjection.Problem(Fixture(), true, false));
            StringAssert.Contains("camera", NetMapProjection.Problem(Fixture(), false, true));
        }

        [Test]
        public void AMapOverTheLimits_IsRefusedBeforeItIsSent()
        {
            var layout = new MissionLayout(0, 1, NetMap.MaxSide + 1, 4, new bool[(NetMap.MaxSide + 1) * 4])
            {
                FloorRects = new[] { new RectInt(0, 0, 1, 1) },
            };
            Assert.That(NetMapProjection.Map(layout, Array.Empty<Vector2Int>(), Fixture(), false, false, out var refusal), Is.Null);
            StringAssert.Contains("grid", refusal);
        }

        [Test]
        public void TheProjectedTypes_HaveNoMemberForSeedSpawnRegionRoomHashAttemptOrContents()
        {
            var forbidden = new[] { "seed", "spawn", "region", "room", "hash", "attempt", "plan", "layout", "hostile", "item", "loot", "content" };
            foreach (var type in new[] { typeof(NetMap), typeof(NetMapBox), typeof(NetObjective) })
            {
                var names = type.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Select(m => m.Name);
                foreach (var name in names)
                    foreach (var bad in forbidden)
                        Assert.That(name.IndexOf(bad, StringComparison.OrdinalIgnoreCase), Is.LessThan(0), type.Name + "." + name);
            }
        }

        static List<MissionObjective> Goals(out EliminateHostilesObjective eliminate, out ReachZoneObjective extraction)
        {
            eliminate = new EliminateHostilesObjective("eliminate", "Eliminate security team", new List<Health>());
            extraction = new ReachZoneObjective("extract", "Extraction", new Vector3(-9.5f, 0f, 4.5f), MissionContent.ZoneRadius, 1, new List<Health>(), isRequired: false);
            return new List<MissionObjective> { eliminate, extraction };
        }

        [Test]
        public void Objectives_CarryTitlesNotCounts_AndAPointOnlyForExtraction()
        {
            var list = NetMapProjection.Objectives(Goals(out _, out _), Fixture(), false, false);
            Assert.That(list.Count, Is.EqualTo(2));
            Assert.That(list[0].Kind, Is.EqualTo(NetObjectiveKind.Eliminate));
            Assert.That(list[0].Title, Is.EqualTo("Eliminate security team"));
            Assert.That(list[0].HasPoint, Is.False);
            Assert.That(list[1].Kind, Is.EqualTo(NetObjectiveKind.Extraction));
            Assert.That(list[1].Point, Is.EqualTo(new Vector3(-9.5f, 0f, 4.5f)));
            Assert.That(list[1].Radius, Is.EqualTo(MissionContent.ZoneRadius));
            Assert.That(NetMapValidation.Problem(list), Is.Null);
        }

        [Test]
        public void AnUnknownObjective_IsLeftOut()
        {
            var goals = Goals(out var eliminate, out _);
            eliminate.SetKnown(false);
            Assert.That(NetMapProjection.Objectives(goals, Fixture(), false, false).Select(o => o.Kind), Is.EqualTo(new[] { NetObjectiveKind.Extraction }));
        }

        [Test]
        public void ALongTitle_IsClippedToTheLimit()
        {
            var goals = new List<MissionObjective> { new EliminateHostilesObjective("e", new string('x', 80), new List<Health>()) };
            Assert.That(NetMapProjection.Objectives(goals, Fixture(), false, false)[0].Title.Length, Is.EqualTo(NetObjective.MaxTitle));
        }
    }
}
