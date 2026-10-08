using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class LeakTextTests
    {
        [Test]
        public void TheSidesLine_WithHostilesHidden_NamesNoHostileCount()
        {
            var text = PrototypeHud.DescribeSidesHidden(3, 3);
            Assert.That(text, Does.Contain("Friendlies alive 3/3"));
            Assert.That(text, Does.Not.Match(@"Hostiles alive \d"));
            Assert.That(text, Does.Contain("Hostiles: unknown"));
        }

        [Test]
        public void TheDebugText_WhenHidden_KeepsOnlyTheStateAndTheSeed()
        {
            var report = new MissionReport
            {
                Seed = 77, AttemptsMade = 2, MaxAttempts = 20, Rooms = 6, Connections = 7, FloorTiles = 400, NavArea = 380f,
                PathsChecked = 12, CoverTotal = 50, FriendlySpawns = 3, HostileSpawns = 3, TeamSeparation = 20f, Bounds = new Bounds(Vector3.zero, new Vector3(50f, 3f, 40f)),
            };
            var hidden = MissionDebugText.DescribeHidden(MissionState.Ready, report);
            Assert.That(hidden, Does.Contain("Mission: Ready | Seed 77"));
            Assert.That(hidden, Does.Contain("attempt 2/20"));
            Assert.That(hidden, Does.Not.Contain("rooms").And.Not.Contain("Spawns").And.Not.Contain("hostile").And.Not.Contain("Cover"));
            Assert.That(MissionDebugText.Describe(MissionState.Ready, report), Does.Contain("Spawns 3 friendly / 3 hostile"), "the full text is unchanged");
        }

        [Test]
        public void ShowsUnitLabel_IsAlwaysTrueForFriendliesAndForAMissingService()
        {
            Assert.That(PrototypeHud.ShowsUnitLabel(null, null, hostile: true), Is.True, "no service: everything known");
            Assert.That(PrototypeHud.ShowsUnitLabel(null, null, hostile: false), Is.True);
        }
    }
}
