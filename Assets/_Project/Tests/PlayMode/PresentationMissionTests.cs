#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PresentationMissionTests
    {
        MissionRig rig;
        Material fogMaterial;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            if (fogMaterial != null) Object.DestroyImmediate(fogMaterial);
        }

        FogPresenter AddFog(IntelligenceService service)
        {
            fogMaterial = new Material(Shader.Find("Sprites/Default"));
            var host = rig.World.Track(new GameObject("FogHost"));
            host.SetActive(false);
            var fog = host.AddComponent<FogPresenter>();
            fog.Initialize(service, fogMaterial, null);
            host.SetActive(true);
            return fog;
        }

        [UnityTest]
        public IEnumerator ABlindMission_HidesItsHostilesAndItsCameras_AndFogsMostOfTheMap()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            var fog = AddFog(service);
            yield return rig.Generate(12345);
            yield return null;

            foreach (var hostile in rig.Director.Hostiles)
            {
                Assert.That(hostile.GetComponent<HostilePresenter>(), Is.Not.Null, hostile.name);
                Assert.That(hostile.GetComponentsInChildren<Renderer>().All(r => r.forceRenderingOff), Is.True, hostile.name);
            }
            foreach (var friendly in rig.Director.Friendlies)
                Assert.That(friendly.GetComponentsInChildren<Renderer>().Any(r => r.forceRenderingOff), Is.False, "the squad is always drawn");
            foreach (var camera in rig.Director.Current.Network.Cameras)
                Assert.That(camera.Visual.GetComponentsInChildren<Renderer>().All(r => r.forceRenderingOff), Is.True, "undiscovered cameras are not drawn");

            var active = Enumerable.Range(0, fog.VolumeCount).Count(fog.IsVolumeActive);
            Assert.That(active, Is.GreaterThan(fog.VolumeCount / 2));
            Assert.That(rig.Director.Current.Root.transform.Find("Fog"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator AFullKnowledgeMission_DrawsEverything_AndBuildsNoActiveFog()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Full());
            var fog = AddFog(service);
            yield return rig.Generate(12345);
            yield return null;
            foreach (var hostile in rig.Director.Hostiles)
                Assert.That(hostile.GetComponentsInChildren<Renderer>().Any(r => r.forceRenderingOff), Is.False);
            Assert.That(Enumerable.Range(0, fog.VolumeCount).Any(fog.IsVolumeActive), Is.False);
        }

        [UnityTest]
        public IEnumerator Regenerating_LeavesNoFogAndNoMarkersFromTheLastMission()
        {
            rig = new MissionRig();
            var service = rig.AddIntelligence(IntelligenceSettings.Blind());
            AddFog(service);
            yield return rig.Generate(12345);
            yield return rig.Generate(31);
            yield return null;
            Assert.That(Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name == "Fog"), Is.EqualTo(1));
            Assert.That(Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name == "LastKnownMarker"), Is.EqualTo(0));
        }
    }
}
#endif
