#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The fog of war on screen: one collider-free volume under the mission root darkens the floor plan by a brightness grid
    /// (hidden 0, dim for what was seen before, clear for what is in sight) that is feathered and eased over time. These
    /// tests read the grid; the renders are checked by eye. Test layout: room A is world x -9..-3, room B x 3..9, z -4..2.
    /// </summary>
    public class FogPresenterTests
    {
        static readonly Vector3 RoomACentre = new Vector3(-6f, 0f, -1f);
        static readonly Vector3 RoomBCentre = new Vector3(6f, 0f, -1f);
        static readonly Vector3 GapWall = new Vector3(0f, 0f, 3f);        // between the rooms, beside both
        static readonly Vector3 EastWall = new Vector3(9.5f, 0f, -1f);    // beside room B only

        IntelRig rig;
        FogPresenter fog;
        Material fogMaterial;
        Transform root;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            if (fogMaterial != null) Object.DestroyImmediate(fogMaterial);
        }

        void Build(bool corridor, IntelligenceSettings settings)
        {
            rig = new IntelRig(corridor);
            rig.AddHostile(IntelRig.InLineGround);
            fogMaterial = new Material(Shader.Find("Sprites/Default")) { name = "TestFog" };
            var host = rig.World.Track(new GameObject("FogHost"));
            host.SetActive(false);
            fog = host.AddComponent<FogPresenter>();
            fog.Initialize(rig.Service, fogMaterial);
            host.SetActive(true);
            root = rig.World.Track(new GameObject("MissionRoot")).transform;
            rig.Begin(settings, new IntelligenceMission { Root = root });
        }

        [UnityTest]
        public IEnumerator TheFogIsOneColliderFreeVolume_UnderTheMissionRoot()
        {
            Build(true, IntelRig.Fog());
            yield return null;
            var container = root.Find("Fog");
            Assert.That(container, Is.Not.Null, "built under the mission root");
            Assert.That(container.childCount, Is.EqualTo(1), "one volume, not one per room");
            Assert.That(root.GetComponentsInChildren<Collider>(), Is.Empty, "fog never blocks a ray, a click or the NavMesh");
            Assert.That(fog.IsShown, Is.True);
        }

        [UnityTest]
        public IEnumerator ObservedGroundIsClear_UnknownGroundIsDark()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.TargetAt(RoomACentre), Is.EqualTo(1f), "room A: the squad is in it");
            Assert.That(fog.TargetAt(RoomBCentre), Is.EqualTo(0f), "room B: unknown behind a closed wall");
        }

        [UnityTest]
        public IEnumerator AMissionOpens_WithTheSquadsRoomAlreadyClear_NotFadingIn()
        {
            Build(false, IntelRig.Fog());
            Assert.That(fog.CurrentAt(RoomACentre), Is.EqualTo(1f), "no flash of black at the start of a mission");
            Assert.That(fog.CurrentAt(RoomBCentre), Is.EqualTo(0f));
            Assert.That(fog.IsSettled, Is.True);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ADiscoveredRegion_IsDim_NotDark_AndTheRevealEasesIn()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.CurrentAt(RoomBCentre), Is.EqualTo(0f));

            rig.Service.Model.RevealRegion(1);
            Assert.That(fog.TargetAt(RoomBCentre), Is.EqualTo(FogPresenter.DiscoveredLevel).Within(0.001f));
            Assert.That(fog.CurrentAt(RoomBCentre), Is.EqualTo(0f), "the display has not moved yet: it eases, it does not pop");
            Assert.That(fog.IsSettled, Is.False);

            yield return TestWorld.WaitUntil(() => fog.IsSettled, 3f);
            Assert.That(fog.IsSettled, Is.True);
            Assert.That(fog.CurrentAt(RoomBCentre), Is.EqualTo(FogPresenter.DiscoveredLevel).Within(0.001f));
        }

        [UnityTest]
        public IEnumerator AWall_TakesTheBrightestRegionBesideIt()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.TargetAt(GapWall), Is.EqualTo(1f), "a wall of a known room is a wall the player can see");
            Assert.That(fog.TargetAt(EastWall), Is.EqualTo(0f), "a wall only beside an unknown room is hidden");

            rig.Service.Model.RevealRegion(1);
            Assert.That(fog.TargetAt(EastWall), Is.EqualTo(FogPresenter.DiscoveredLevel).Within(0.001f));
        }

        [UnityTest]
        public IEnumerator TheEdgeOfTheDark_IsFeathered_IntoTheKnownSide_AndNeverIntoTheHiddenSide()
        {
            Build(true, IntelRig.Fog(s => s.observationRange = 1f));
            yield return null;
            Assert.That(rig.Service.StateOfRegion(2), Is.EqualTo(KnowledgeState.Unknown), "Precondition: the corridor is unseen");
            var inCorridor = fog.TargetAt(new Vector3(-2.5f, 0f, -1.5f));
            var justInsideA = fog.TargetAt(new Vector3(-3.4f, 0f, -1.5f));
            var deepInsideA = fog.TargetAt(new Vector3(-8f, 0f, -1.5f));
            Assert.That(inCorridor, Is.EqualTo(0f), "hidden ground stays hidden");
            Assert.That(justInsideA, Is.GreaterThan(0f).And.LessThan(0.5f), "the rim of the known room fades into the dark");
            Assert.That(deepInsideA, Is.EqualTo(1f), "the rest of the room is clear");
        }

        [UnityTest]
        public IEnumerator TruthView_ShowsEverything_AndFogOffNeverFogs()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.IsShown, Is.True);
            rig.Service.TruthView = true;
            Assert.That(fog.IsShown, Is.False, "truth view");
            rig.Service.TruthView = false;
            Assert.That(fog.IsShown, Is.True);

            rig.Service.Begin(new IntelligenceMission { Layout = rig.Layout, Root = root }, new IntelligenceSettings(), rig.Encounter);
            yield return null;
            Assert.That(fog.IsShown, Is.False, "fog off");
        }

        [UnityTest]
        public IEnumerator ABeginningMission_ReplacesTheOldVolume()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var old = root.Find("Fog").gameObject;
            rig.Service.Begin(new IntelligenceMission { Layout = rig.Layout, Root = root }, IntelRig.Fog(), rig.Encounter);
            yield return null;
            Assert.That(old == null, Is.True, "the previous mission's volume is destroyed");
            Assert.That(root.Cast<Transform>().Count(t => t.name == "Fog"), Is.EqualTo(1));
            Assert.That(fog.TargetAt(RoomBCentre), Is.EqualTo(0f), "the new mission starts from its own briefing");
            Assert.That(fog.CurrentAt(RoomBCentre), Is.EqualTo(0f), "a new mission starts dark at once, not fading in");
        }

        // The volume is destroyed with the mission root by the director; the presenter's contract on Clear is only that
        // nothing stays on screen (the service then reports "no fog").
        [UnityTest]
        public IEnumerator WhenTheMissionIsCleared_NoFogStaysOn()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.IsShown, Is.True);
            rig.Service.Clear();
            Assert.That(fog.IsShown, Is.False);
        }

        [UnityTest]
        public IEnumerator TheFogMaterial_IsGivenTheGridAndItsArea_AndTheSharedAssetIsLeftAlone()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(fog.Material, Is.Not.Null);
            Assert.That(fog.Material, Is.Not.SameAs(fogMaterial), "the presenter uses its own copy, never the shared asset");
            Assert.That(fog.VisibilityTexture, Is.Not.Null);
            Assert.That(fog.VisibilityTexture.format, Is.EqualTo(TextureFormat.R8));
            Assert.That(fog.VisibilityTexture.wrapMode, Is.EqualTo(TextureWrapMode.Clamp));
        }
    }
}
#endif
