#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class IntelligenceDebugTextTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [Test]
        public void WithoutAMission_ItSaysSo()
        {
            rig = new IntelRig(corridor: false);
            Assert.That(IntelligenceDebugText.Describe(rig.Service, new Health[0], new MissionObjective[0]), Does.Contain("no mission"));
        }

        [UnityTest]
        public IEnumerator ItShowsActualVersusKnown_ForRegionsEnemiesAndObjectives()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            hostile.name = "Hostile_1";
            rig.Begin(IntelRig.Fog());
            yield return null;
            var goal = new ObjectiveStub("hack");
            goal.SetKnown(false);

            var text = IntelligenceDebugText.Describe(rig.Service, rig.Hostiles, new MissionObjective[] { goal });
            Assert.That(text, Does.Contain("INTEL TRUTH VIEW"));
            Assert.That(text, Does.Contain("fog=True"));
            Assert.That(text, Does.Contain("R0 Room known=Observed hostiles(actual)=0"));
            Assert.That(text, Does.Contain("R1 Room known=Unknown hostiles(actual)=1"));
            Assert.That(text, Does.Contain("Hostile_1: Unknown actual=(6.5, -1.5)"));
            Assert.That(text, Does.Contain("hack: UNKNOWN"));
        }

        [UnityTest]
        public IEnumerator ItShowsTheCameraNetwork_AndRunningScans()
        {
            rig = new IntelRig(corridor: false);
            var mount = new CameraMount(new Vector2Int(18, 3), new Vector2Int(1, 0), 1);
            var security = new SecurityPlan(true, 0, new Vector2Int(2, 2), new[] { mount });
            var spec = new CameraSpec(1, security.CameraPosition(rig.Layout, 0), security.CameraForward(0), 12f, 45f);
            var network = new CameraNetwork(new[] { spec });
            rig.Begin(IntelRig.Fog(), new IntelligenceMission { Security = security, Network = network });
            yield return null;
            var before = IntelligenceDebugText.Describe(rig.Service, new Health[0], new MissionObjective[0]);
            Assert.That(before, Does.Contain("Cameras: intact x1"));
            Assert.That(before, Does.Contain("Scans: none"));

            network.Compromise();
            rig.Service.Scan(IntelRig.InLineGround, 3f, 4f);
            var after = IntelligenceDebugText.Describe(rig.Service, new Health[0], new MissionObjective[0]);
            Assert.That(after, Does.Contain("Cameras: hacked x1"));
            Assert.That(after, Does.Contain("Scans: 1"));
        }

        sealed class ObjectiveStub : MissionObjective
        {
            public ObjectiveStub(string id) : base(id, ObjectiveType.Interact, id, true) { }
            public override string Describe() => Title;
        }
    }
}
#endif
