using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class MissionDebugTextTests
    {
        static MissionReport Ready() => new MissionReport
        {
            Seed = 12345, Attempt = 2, AttemptsMade = 2, MaxAttempts = 20, Succeeded = true, Rooms = 6, Connections = 6,
            FloorTiles = 520, NavArea = 311.4f, PathsChecked = 15, CoverTotal = 180, CoverLow = 70, CoverTall = 110, CoverCorner = 24, CoverColumn = 12,
            FriendlySpawns = 3, HostileSpawns = 3, TeamSeparation = 21.3f, Bounds = new Bounds(Vector3.zero, new Vector3(50f, 3f, 38f)),
        };

        [Test]
        public void Ready_ShowsSeedAttemptStructureNavigationCoverSpawnsAndBounds()
        {
            var text = MissionDebugText.Describe(MissionState.Ready, Ready());
            foreach (var part in new[] { "Seed 12345", "attempt 2/20", "Ready", "6 rooms", "6 connections", "Nav 311", "15 paths ok",
                         "Cover 180", "low 70", "tall 110", "corner 24", "column 12", "Spawns 3 friendly / 3 hostile", "21.3 m apart", "50 x 38" })
                Assert.That(text, Does.Contain(part));
        }

        [Test]
        public void Failed_ShowsTheFailureAndTheSeed()
        {
            var report = new MissionReport { Seed = 9, AttemptsMade = 3, MaxAttempts = 3, Failure = "Mission generation failed. seed=9 attempts=3" };
            var text = MissionDebugText.Describe(MissionState.Failed, report);
            Assert.That(text, Does.Contain("Failed").And.Contain("Seed 9").And.Contain("seed=9 attempts=3"));
        }

        [Test]
        public void Generating_SaysSo_WithoutNumbersThatDoNotExistYet()
        {
            var text = MissionDebugText.Describe(MissionState.Generating, new MissionReport { Seed = 5, MaxAttempts = 20 });
            Assert.That(text, Does.Contain("Generating").And.Contain("Seed 5"));
            Assert.That(text, Does.Not.Contain("rooms"));
        }

        [Test]
        public void Hints_NameBothDeveloperKeys() =>
            Assert.That(MissionDebugText.Hints, Does.Contain("F6").And.Contain("F7"));
    }
}
