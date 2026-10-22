using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetPlayTextTests
    {
        [Test]
        public void ObjectiveLines_SayTheStateInWords()
        {
            Assert.That(NetPlayText.ObjectiveLine(new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Active, "Eliminate security team", false, Vector3.zero, 0f)),
                Is.EqualTo("[ ] Eliminate security team"));
            Assert.That(NetPlayText.ObjectiveLine(new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Completed, "Eliminate security team", false, Vector3.zero, 0f)),
                Is.EqualTo("[x] Eliminate security team"));
            Assert.That(NetPlayText.ObjectiveLine(new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Inactive, "Extraction", true, Vector3.zero, 2f)),
                Is.EqualTo("[ ] Extraction (locked)"));
            Assert.That(NetPlayText.ObjectiveLine(new NetObjective(NetObjectiveKind.Extraction, ObjectiveState.Active, "Extraction", true, Vector3.zero, 2f)),
                Is.EqualTo("[ ] Extraction (open: reach the green zone)"));
            Assert.That(NetPlayText.ObjectiveLine(new NetObjective(NetObjectiveKind.Eliminate, ObjectiveState.Failed, "X", false, Vector3.zero, 0f)),
                Is.EqualTo("[!] X (failed)"));
        }

        [Test]
        public void TheBanner_NamesTheResult()
        {
            Assert.That(NetPlayText.ResultBanner(NetMissionResult.Success), Is.EqualTo("MISSION SUCCESS"));
            Assert.That(NetPlayText.ResultBanner(NetMissionResult.Failure), Is.EqualTo("MISSION FAILED"));
            Assert.That(NetPlayText.ResultBanner(NetMissionResult.None), Is.Null);
        }

        [Test]
        public void MapStatus_ExplainsAMissingOrRefusedMap()
        {
            var record = new NetClientRecord();
            Assert.That(NetPlayText.MapStatus(record), Is.EqualTo("No map authorized for this mission"));
            record.ReceiveMap(0d, null, "map too large");
            Assert.That(NetPlayText.MapStatus(record), Is.EqualTo("Map refused: map too large"));
        }

        [Test]
        public void HudPanels_CoverTheirScreenRects()
        {
            Assert.That(NetPlayText.Covers(NetPlayText.ObjectivesPanel.center, 2, 800f), Is.True);
            var squad = NetPlayText.SquadPanel(2, 800f);
            Assert.That(NetPlayText.Covers(squad.center, 2, 800f), Is.True);
            Assert.That(NetPlayText.Covers(new Vector2(640f, 400f), 2, 800f), Is.False, "the middle of the screen is the world");
        }
    }
}
