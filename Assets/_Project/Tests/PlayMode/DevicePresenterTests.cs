#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class DevicePresenterTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator ADevice_IsHiddenUntilItsDeviceIsDiscovered_AndShownInTruthView()
        {
            rig = new IntelRig(corridor: false);
            var visual = rig.World.Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            var security = new SecurityPlan(true, 0, new Vector2Int(19, 9), new CameraMount[0]);
            rig.Begin(IntelRig.Fog(), new IntelligenceMission { Security = security });
            visual.AddComponent<DevicePresenter>().Bind(rig.Service, SecurityPlan.TerminalDeviceId);
            var renderer = visual.GetComponent<Renderer>();
            yield return null;
            Assert.That(renderer.forceRenderingOff, Is.True);

            rig.Service.Model.RevealDevice(SecurityPlan.TerminalDeviceId);
            yield return null;
            Assert.That(renderer.forceRenderingOff, Is.False);

            rig.Service.Clear();
            yield return null;
            Assert.That(renderer.forceRenderingOff, Is.False, "no mission: everything shown");
        }

        [UnityTest]
        public IEnumerator TruthView_ShowsAnUndiscoveredDevice()
        {
            rig = new IntelRig(corridor: false);
            var visual = rig.World.Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            var security = new SecurityPlan(true, 0, new Vector2Int(19, 9), new CameraMount[0]);
            rig.Begin(IntelRig.Fog(), new IntelligenceMission { Security = security });
            visual.AddComponent<DevicePresenter>().Bind(rig.Service, SecurityPlan.TerminalDeviceId);
            yield return null;
            rig.Service.TruthView = true;
            yield return null;
            Assert.That(visual.GetComponent<Renderer>().forceRenderingOff, Is.False);
        }
    }
}
#endif
