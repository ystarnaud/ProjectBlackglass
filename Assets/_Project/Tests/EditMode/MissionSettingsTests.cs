using NUnit.Framework;

namespace Blackglass.Tests
{
    public class MissionSettingsTests
    {
        [Test]
        public void Defaults_AreTheValuesTheSpecNames()
        {
            var s = new MissionSettings();
            Assert.That((s.gridColumns, s.gridRows, s.cellSize, s.roomCount), Is.EqualTo((4, 3, 14, 6)));
            Assert.That((s.corridorWidth, s.extraLoops, s.bafflesPerRoom), Is.EqualTo((3, 1, 1)));
            Assert.That((s.friendlyCount, s.hostileCount, s.maxAttempts), Is.EqualTo((3, 3, 20)));
            Assert.That((s.lowCoverDensity, s.minTeamSeparation), Is.EqualTo((3f, 16f)));
        }

        [Test]
        public void Validated_ClampsEveryFieldIntoItsRange_AndLeavesTheOriginalAlone()
        {
            var raw = new MissionSettings
            {
                gridColumns = 99, gridRows = 0, cellSize = 3, roomCount = 500, corridorWidth = 1, extraLoops = -4,
                bafflesPerRoom = 40, lowCoverDensity = 900f, friendlyCount = 0, hostileCount = 99, minTeamSeparation = -5f,
                maxAttempts = 0,
            };
            var v = raw.Validated();
            Assert.That(v.gridColumns, Is.EqualTo(6));
            Assert.That(v.gridRows, Is.EqualTo(2));
            Assert.That(v.cellSize, Is.EqualTo(12));
            Assert.That(v.roomCount, Is.EqualTo(v.gridColumns * v.gridRows));
            Assert.That(v.corridorWidth, Is.EqualTo(3));
            Assert.That(v.extraLoops, Is.EqualTo(0));
            Assert.That(v.bafflesPerRoom, Is.EqualTo(3));
            Assert.That(v.lowCoverDensity, Is.EqualTo(10f));
            Assert.That(v.friendlyCount, Is.EqualTo(1));
            Assert.That(v.hostileCount, Is.EqualTo(8));
            Assert.That(v.minTeamSeparation, Is.EqualTo(0f));
            Assert.That(v.maxAttempts, Is.EqualTo(1));
            Assert.That(raw.gridColumns, Is.EqualTo(99), "the original is not modified");
        }

        [Test]
        public void Validated_KeepsAtLeastTwoRooms_EvenOnTheSmallestGrid()
        {
            var v = new MissionSettings { gridColumns = 2, gridRows = 2, roomCount = 1 }.Validated();
            Assert.That(v.roomCount, Is.EqualTo(2));
        }

        [Test]
        public void Describe_NamesTheSeedAndEverySetting()
        {
            var text = new MissionSettings { seed = 777 }.Describe();
            foreach (var part in new[] { "seed=777", "grid=4x3", "cell=14", "rooms=6", "corridor=3", "loops=1", "baffles=1",
                         "lowCover=3", "friendlies=3", "hostiles=3", "separation=16", "attempts=20" })
                Assert.That(text, Does.Contain(part));
        }
    }
}
