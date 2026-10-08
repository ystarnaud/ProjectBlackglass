#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class HostilePresenterTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        HostilePresenter Present(Health hostile, Transform markerParent = null)
        {
            var presenter = hostile.gameObject.AddComponent<HostilePresenter>();
            presenter.Bind(rig.Service, markerParent);
            return presenter;
        }

        static bool AllRenderingOff(Health hostile) =>
            hostile.GetComponentsInChildren<Renderer>().All(r => r.forceRenderingOff);

        static bool AllRenderingOn(Health hostile) =>
            hostile.GetComponentsInChildren<Renderer>().All(r => !r.forceRenderingOff);

        [UnityTest]
        public IEnumerator AnUnobservedHostile_IsNotDrawn_AndColliderAndAIAreUntouched()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            var presenter = Present(hostile);
            yield return null;
            Assert.That(presenter.IsHidden, Is.True);
            Assert.That(AllRenderingOff(hostile), Is.True);
            Assert.That(hostile.gameObject.activeSelf, Is.True, "hidden is not disabled");
            Assert.That(hostile.GetComponent<Collider>().enabled, Is.True, "the collider stays: only knowledge hides it");
        }

        [UnityTest]
        public IEnumerator AnObservedHostile_IsDrawn()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            var presenter = Present(hostile);
            yield return null;
            Assert.That(presenter.IsHidden, Is.False);
            Assert.That(AllRenderingOn(hostile), Is.True);
        }

        [UnityTest]
        public IEnumerator ItIsHiddenAgain_WhenItLeavesObservation_AndAMarkerStaysWhereItWasLastSeen()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            var parent = rig.World.Track(new GameObject("Actors")).transform;
            var presenter = Present(hostile, parent);
            yield return null;
            Assert.That(presenter.Marker == null, Is.True, "no marker while it is in sight");

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            yield return null;
            Assert.That(presenter.IsHidden, Is.True);
            Assert.That(presenter.Marker, Is.Not.Null);
            Assert.That(presenter.Marker.activeSelf, Is.True);
            Assert.That(presenter.Marker.transform.IsChildOf(parent), Is.True, "destroyed with the mission");
            Assert.That(presenter.Marker.GetComponentsInChildren<Collider>(), Is.Empty, "a marker is not a target and blocks nothing");
            Assert.That(Vector3.Distance(Flat(presenter.Marker.transform.position), Flat(IntelRig.InLineGround)), Is.LessThan(0.01f));

            hostile.transform.position = new Vector3(8f, 1f, 1.5f);   // walks off unseen: the marker does not follow
            rig.Service.RunPass();
            yield return null;
            Assert.That(Vector3.Distance(Flat(presenter.Marker.transform.position), Flat(IntelRig.InLineGround)), Is.LessThan(0.01f));
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        [UnityTest]
        public IEnumerator ADeadHostile_LeavesNoMarkerAndNoException()
        {
            rig = new IntelRig(corridor: false);   // closed wall: the hostile is not in sight, so its briefing marker shows
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.enemyMarkersAtStart = 1));
            var presenter = Present(hostile);
            yield return null;
            Assert.That(presenter.Marker, Is.Not.Null, "Precondition: a briefing marker");
            hostile.TakeDamage(1000);
            yield return null;
            Assert.That(presenter.Marker == null || !presenter.Marker.activeSelf, Is.True);
        }

        [UnityTest]
        public IEnumerator TruthView_ShowsTheHostileAndNoMarker_ButDoesNotMakeItATarget()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            var presenter = Present(hostile);
            yield return null;
            Assert.That(presenter.IsHidden, Is.True);
            rig.Service.TruthView = true;
            yield return null;
            Assert.That(presenter.IsHidden, Is.False);
            Assert.That(AllRenderingOn(hostile), Is.True);
            Assert.That(rig.Service.CanTarget(hostile), Is.False, "truth view is display only");
        }

        [UnityTest]
        public IEnumerator FogOff_DrawsEveryHostile()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(new IntelligenceSettings());
            var presenter = Present(hostile);
            yield return null;
            Assert.That(presenter.IsHidden, Is.False);
        }

        [UnityTest]
        public IEnumerator AnAttackLineFromAHiddenHostile_IsNotDrawn()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            var line = new GameObject("Line").AddComponent<LineRenderer>();
            line.transform.SetParent(hostile.transform, false);
            line.positionCount = 2;
            line.enabled = true;
            rig.Begin(IntelRig.Fog());
            Present(hostile);
            yield return null;
            Assert.That(line.forceRenderingOff, Is.True, "line renderers under the hostile are hidden with it");
        }
    }
}
#endif
