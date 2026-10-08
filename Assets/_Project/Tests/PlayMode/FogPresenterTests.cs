#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class FogPresenterTests
    {
        IntelRig rig;
        FogPresenter fog;
        Material fogMaterial;
        Material veilMaterial;
        Transform root;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            if (fogMaterial != null) Object.DestroyImmediate(fogMaterial);
            if (veilMaterial != null) Object.DestroyImmediate(veilMaterial);
        }

        void Build(bool corridor, IntelligenceSettings settings, bool withVeil = true)
        {
            rig = new IntelRig(corridor);
            rig.AddHostile(IntelRig.InLineGround);
            fogMaterial = new Material(Shader.Find("Sprites/Default")) { name = "TestFog" };
            veilMaterial = new Material(Shader.Find("Sprites/Default")) { name = "TestVeil" };
            var host = rig.World.Track(new GameObject("FogHost"));
            host.SetActive(false);
            fog = host.AddComponent<FogPresenter>();
            fog.Initialize(rig.Service, fogMaterial, withVeil ? veilMaterial : null);
            host.SetActive(true);
            root = rig.World.Track(new GameObject("MissionRoot")).transform;
            rig.Begin(settings, new IntelligenceMission { Root = root });
        }

        // Region volumes come first (ids 0..2 for a corridor layout, 0..1 without), wall volumes after.
        int RegionVolumes => rig.Service.Map.Count;

        [UnityTest]
        public IEnumerator EveryRegionAndEveryWallBoxGetsAColliderFreeVolume_UnderTheMissionRoot()
        {
            Build(true, IntelRig.Fog());
            yield return null;
            Assert.That(fog.VolumeCount, Is.EqualTo(RegionVolumes + 3), "three regions and three wall boxes");
            Assert.That(root.Find("Fog"), Is.Not.Null, "built under the mission root");
            Assert.That(root.GetComponentsInChildren<Collider>(), Is.Empty, "fog never blocks a ray, a click or the NavMesh");
        }

        [UnityTest]
        public IEnumerator UnknownRegionsAreFogged_ObservedOnesAreClear_DiscoveredOnesAreVeiled()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.IsVolumeActive(0), Is.False, "room A: the squad is in it, observed");
            Assert.That(fog.IsVolumeActive(1), Is.True, "room B: unknown behind a closed wall");
            Assert.That(fog.VolumeMaterial(1), Is.SameAs(fogMaterial));

            rig.Service.Model.RevealRegion(1);
            Assert.That(fog.IsVolumeActive(1), Is.True, "discovered, not in sight: a veil");
            Assert.That(fog.VolumeMaterial(1), Is.SameAs(veilMaterial));
        }

        [UnityTest]
        public IEnumerator WithoutAVeilMaterial_ADiscoveredRegionIsSimplyClear()
        {
            Build(false, IntelRig.Fog(), withVeil: false);
            yield return null;
            rig.Service.Model.RevealRegion(1);
            Assert.That(fog.IsVolumeActive(1), Is.False);
        }

        [UnityTest]
        public IEnumerator AWallVolume_IsShownOnlyWhileEveryRegionBesideItIsUnknown()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var walls = Enumerable.Range(0, fog.VolumeCount).Where(fog.IsWallVolume).ToArray();
            Assert.That(walls.Length, Is.EqualTo(2), "the gap wall and the wall east of room B");
            var gap = walls[0];       // beside rooms A and B: A is observed
            var east = walls[1];      // beside room B only
            Assert.That(fog.IsVolumeActive(gap), Is.False, "a wall of a known room is a wall the player can see");
            Assert.That(fog.IsVolumeActive(east), Is.True, "a wall only beside an unknown room is hidden");

            rig.Service.Model.RevealRegion(1);
            Assert.That(fog.IsVolumeActive(east), Is.False);
        }

        [UnityTest]
        public IEnumerator TruthView_ShowsEverything_AndFogOffNeverFogs()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.IsVolumeActive(1), Is.True);
            rig.Service.TruthView = true;
            for (var i = 0; i < fog.VolumeCount; i++)
                Assert.That(fog.IsVolumeActive(i), Is.False, $"volume {i} in truth view");
            rig.Service.TruthView = false;
            Assert.That(fog.IsVolumeActive(1), Is.True);

            rig.Service.Begin(new IntelligenceMission { Layout = rig.Layout, Root = root }, new IntelligenceSettings(), rig.Encounter);
            yield return null;
            for (var i = 0; i < fog.VolumeCount; i++)
                Assert.That(fog.IsVolumeActive(i), Is.False, "fog off");
        }

        [UnityTest]
        public IEnumerator ABeginningMission_ReplacesTheOldVolumes()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var old = root.Find("Fog").gameObject;
            rig.Service.Begin(new IntelligenceMission { Layout = rig.Layout, Root = root }, IntelRig.Fog(), rig.Encounter);
            yield return null;
            Assert.That(old == null, Is.True, "the previous mission's volumes are destroyed");
            Assert.That(root.Cast<Transform>().Count(t => t.name == "Fog"), Is.EqualTo(1));
        }

        // The volumes are destroyed with the mission root by the director; the presenter's contract on Clear is only that no
        // volume stays on (the service then reports "no fog").
        [UnityTest]
        public IEnumerator WhenTheMissionIsCleared_NoVolumeStaysOn()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.IsVolumeActive(1), Is.True);
            rig.Service.Clear();
            for (var i = 0; i < fog.VolumeCount; i++)
                Assert.That(fog.IsVolumeActive(i), Is.False);
        }
    }
}
#endif
