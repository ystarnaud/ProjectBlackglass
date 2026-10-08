#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class IntelMapViewTests
    {
        static readonly Rect Panel = new Rect(0f, 0f, 260f, 190f);
        IntelRig rig;
        IntelMapView view;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        void Build(bool corridor, IntelligenceSettings settings, IntelligenceMission mission = null)
        {
            rig = new IntelRig(corridor);
            rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(settings, mission);
            var host = rig.World.Track(new GameObject("Map"));
            view = host.AddComponent<IntelMapView>();
            view.Initialize(rig.Service, rig.Encounter, null);
        }

        static MapMark[] Of(System.Collections.Generic.IReadOnlyList<MapMark> marks, MapMarkKind kind) => marks.Where(m => m.Kind == kind).ToArray();

        [UnityTest]
        public IEnumerator Regions_AreColouredByWhatThePlayerKnows_UnknownDarkDiscoveredGreyObservedLight()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            rig.Service.Model.RevealRegion(1);
            var regions = Of(view.BuildMarks(Panel), MapMarkKind.Region);
            Assert.That(regions.Length, Is.EqualTo(rig.Service.Map.Count));
            Assert.That(regions[0].Color, Is.EqualTo(IntelMapView.ObservedColor), "room A: in sight");
            Assert.That(regions[1].Color, Is.EqualTo(IntelMapView.DiscoveredColor), "room B: found but not in sight");

            rig.Service.Clear();
            rig.Begin(IntelRig.Fog());
            regions = Of(view.BuildMarks(Panel), MapMarkKind.Region);
            Assert.That(regions[1].Color, Is.EqualTo(IntelMapView.UnknownColor));
        }

        [UnityTest]
        public IEnumerator Enemies_AreDrawnOnlyWhileObserved_AMarkerWhenLastKnown_AndNothingOtherwise()
        {
            Build(true, IntelRig.Fog());
            yield return null;
            var observed = view.BuildMarks(Panel);
            Assert.That(Of(observed, MapMarkKind.Enemy).Length, Is.EqualTo(1), "in line down the corridor");
            Assert.That(Of(observed, MapMarkKind.LastKnown), Is.Empty);

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            var lost = view.BuildMarks(Panel);
            Assert.That(Of(lost, MapMarkKind.Enemy), Is.Empty);
            Assert.That(Of(lost, MapMarkKind.LastKnown).Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AnUnknownEnemy_IsNotOnTheMap_ExceptInTruthView()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Enemy), Is.Empty);
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.LastKnown), Is.Empty);
            rig.Service.TruthView = true;
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Enemy).Length, Is.EqualTo(1));
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Region)[1].Color, Is.EqualTo(IntelMapView.TruthUnknownColor));
        }

        [UnityTest]
        public IEnumerator Friendlies_AreAlwaysOnTheMap()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Friendly).Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Devices_AreDrawnOnlyOnceDiscovered()
        {
            var security = new SecurityPlan(true, 0, new Vector2Int(19, 9), new CameraMount[0]);
            Build(false, IntelRig.Fog(), new IntelligenceMission { Security = security });
            yield return null;
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Device), Is.Empty);
            rig.Service.Model.RevealDevice(SecurityPlan.TerminalDeviceId);
            Assert.That(Of(view.BuildMarks(Panel), MapMarkKind.Device).Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator WithFogOff_TheMapDrawsNothing()
        {
            Build(false, new IntelligenceSettings());
            yield return null;
            Assert.That(view.BuildMarks(Panel), Is.Empty);
        }

        [UnityTest]
        public IEnumerator ToggleHidesAndShowsTheOverlay()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(view.IsVisible, Is.True);
            view.Toggle();
            Assert.That(view.IsVisible, Is.False);
            view.Toggle();
            Assert.That(view.IsVisible, Is.True);
        }
    }
}
#endif
