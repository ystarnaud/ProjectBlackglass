#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CameraImpulsePlayModeTests
    {
        TestWorld world;
        TacticalPause pause;
        TacticalCameraController controller;
        CameraImpulse impulse;
        Camera view;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var rig = world.Track(new GameObject("CameraRig"));
            rig.SetActive(false);
            var cameraObject = new GameObject("Camera");
            cameraObject.transform.SetParent(rig.transform, false);
            view = cameraObject.AddComponent<Camera>();
            controller = rig.AddComponent<TacticalCameraController>();
            controller.Initialize(view, null, null, null, null, null, null);
            impulse = rig.AddComponent<CameraImpulse>();
            controller.SetImpulse(impulse);
            rig.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator WithoutAnImpulse_TheCameraSitsAtItsRestPose()
        {
            yield return null;
            var rest = view.transform.localPosition;
            yield return new WaitForSeconds(0.1f);
            Assert.That(view.transform.localPosition, Is.EqualTo(rest));
        }

        [UnityTest]
        public IEnumerator Impulse_MovesTheViewingCameraOnly_StaysSmall_AndDecaysBackToRest()
        {
            yield return null;
            var rest = view.transform.localPosition;
            var pivot = controller.transform.position;
            impulse.Add(1f);
            yield return null; yield return null;
            Assert.That(Vector3.Distance(view.transform.localPosition, rest), Is.GreaterThan(0f));
            Assert.That(Vector3.Distance(view.transform.localPosition, rest), Is.LessThanOrEqualTo(ImpulseEnvelope.MaxOffset * 1.5f));
            Assert.That(controller.transform.position, Is.EqualTo(pivot), "the pivot the gameplay uses never moves");
            yield return new WaitForSeconds(ImpulseEnvelope.Duration + 0.2f);
            Assert.That(Vector3.Distance(view.transform.localPosition, rest), Is.LessThan(1e-4f));
        }

        [UnityTest]
        public IEnumerator Impulse_FreezesDuringPause_WhileTheCameraControlsKeepWorking()
        {
            yield return null;
            var rest = view.transform.localPosition;
            impulse.Add(1f);
            yield return new WaitForSeconds(0.05f);
            pause.Pause();
            yield return null;
            var frozen = view.transform.localPosition;
            Assert.That(Vector3.Distance(frozen, rest), Is.GreaterThan(1e-6f), "the impulse was still running when the pause hit, so freezing it is a real check");
            var pivotBefore = controller.transform.position;
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(view.transform.localPosition, Is.EqualTo(frozen).Using(new UnityEngine.TestTools.Utils.Vector3EqualityComparer(1e-5f)));

            controller.FocusOn(pivotBefore + new Vector3(3f, 0f, 0f));
            yield return null;
            Assert.That(controller.transform.position.x, Is.EqualTo(pivotBefore.x + 3f).Within(0.01f), "normal camera control still works while paused");
        }

        [UnityTest]
        public IEnumerator PointerRays_AreOffByNoMoreThanTheShakeAmplitude()
        {
            yield return null;
            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Vector3 GroundHit()
            {
                var ray = view.ScreenPointToRay(centre);
                var plane = new Plane(Vector3.up, Vector3.zero);
                Assert.That(plane.Raycast(ray, out var d), Is.True);
                return ray.GetPoint(d);
            }
            var still = GroundHit();
            impulse.Add(1f);
            yield return null; yield return null;
            var shaken = GroundHit();
            Assert.That(Vector3.Distance(still, shaken), Is.LessThanOrEqualTo(ImpulseEnvelope.MaxOffset * 2f),
                "a click during the shake lands within a few centimetres of where it would have");
        }
    }
}
#endif
