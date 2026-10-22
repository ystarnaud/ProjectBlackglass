#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Owner ruling D7: camera input and operative movement stay distinct when follow changes. With the project's real bindings
    /// on a test keyboard: WASD pans the camera and sends nothing while free; with F, WASD steers the primary operative
    /// (NetMoveIntent) and the camera stays on it; Esc releases the intent before WASD pans again.
    /// </summary>
    public class NetPlayFollowPlayTests : InputTestFixture
    {
        Keyboard keyboard;
        InputActionAsset actions;
        NetLoopbackRig rig;
        GameObject cameraObject;
        readonly List<NetRequest> sent = new List<NetRequest>();

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "KeyboardMouse");
        }

        public override void TearDown()
        {
            TestControls.Reset(actions);
            base.TearDown();
        }

        [UnityTearDown]
        public IEnumerator StopRig()
        {
            if (cameraObject != null)
                Object.Destroy(cameraObject);
            if (rig != null)
                yield return rig.Stop();
        }

        IEnumerator Hold(ButtonControl key, int frames)
        {
            Press(key);
            for (var i = 0; i < frames; i++)
                yield return null;
            Release(key);
            yield return null;
        }

        IEnumerator Tap(ButtonControl key) => Hold(key, 1);

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        NetPlayControls controls;
        TacticalCameraController cameraRig;

        // The rig, the drawn map, a camera with the real Pan and Rotate, and the controls with the real Stop, Cancel, Pan,
        // NextCharacter and ToggleFollow; requests are captured instead of sent.
        IEnumerator StartClient()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet(quiet: true);
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var client = rig.Client;
            var view = NetPlayKit.AddMapView(client.gameObject, client, false);
            cameraObject = new GameObject("CameraRig");
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.transform.SetParent(cameraObject.transform, false);
            cameraRig = cameraObject.AddComponent<TacticalCameraController>();
            var pan = TestControls.Ref(actions, "Camera/Pan");
            cameraRig.Initialize(camera, pan, TestControls.Ref(actions, "Camera/Rotate"), null, null, null);
            controls = client.gameObject.AddComponent<NetPlayControls>();
            controls.Initialize(client, camera, cameraRig, view, null, null, null,
                TestControls.Ref(actions, "Commands/Stop"), TestControls.Ref(actions, "Commands/Cancel"), pan,
                TestControls.Ref(actions, "Character/NextCharacter"), TestControls.Ref(actions, "Character/ToggleFollow"));
            controls.Send = r => { sent.Add(r); return (uint)sent.Count; };
            yield return TestWorld.WaitUntil(() => view.Built && controls.Selection.Primary != 0, 10f);
            yield return null;
            sent.Clear();
        }

        static int UnitOf(NetRequest request) =>
            request is NetMoveIntent i ? i.UnitId : request is NetMoveTo m ? m.UnitId : request is NetAttack a ? a.UnitId : request is NetStop s ? s.UnitId : -1;

        // Final review I2 and Review Focus 4: the followed operative dies while steered; follow ends, no other operative is
        // steered in its place, and no request names unit 0.
        [UnityTest]
        public IEnumerator Follow_ends_when_the_followed_operative_dies([Values(1, 2)] int selected)
        {
            yield return StartClient();
            if (selected == 2)
                controls.SelectAllLiving();
            Assert.That(controls.Selection.Count, Is.EqualTo(selected));
            var primary = controls.Selection.Primary;
            yield return Tap(keyboard.fKey);
            Assert.That(controls.Following, Is.True);
            Press(keyboard.wKey);
            for (var i = 0; i < 10; i++)
                yield return null;
            Assert.That(sent.OfType<NetMoveIntent>().Any(i => i.UnitId == primary && i.Direction != Vector3.zero), Is.True, "steering");
            sent.Clear();
            var unit = rig.Director.Friendlies[primary - rig.Client.FirstOwnedId];
            Assert.That(rig.Server.Units.IdOf(unit), Is.EqualTo(primary));
            unit.GetComponent<Health>().TakeDamage(100000);
            yield return TestWorld.WaitUntil(() => NetClient.VisibleStates().Any(s => s.UnitId == primary && !s.Alive), 5f);
            for (var i = 0; i < 20; i++)
                yield return null;
            Release(keyboard.wKey);
            yield return null;
            Assert.That(controls.Following, Is.False, "follow ended with the operative");
            Assert.That(sent.OfType<NetMoveIntent>(), Is.Empty, "nobody is steered in its place, and the dead get no release");
            Assert.That(sent.All(r => UnitOf(r) > 0), Is.True, "no request names unit 0");
        }

        [UnityTest]
        public IEnumerator Wasd_pans_or_steers_never_both()
        {
            yield return StartClient();
            var client = rig.Client;
            var primary = controls.Selection.Primary;

            // Free camera: W pans, and no operative is told anything.
            var before = Flat(cameraRig.transform.position);
            yield return Hold(keyboard.wKey, 20);
            Assert.That(Vector3.Distance(Flat(cameraRig.transform.position), before), Is.GreaterThan(0.2f), "W pans the free camera");
            Assert.That(sent, Is.Empty, "panning sends no request");

            // Follow: W steers the primary; the camera stays on it instead of panning away.
            yield return Tap(keyboard.fKey);
            Assert.That(controls.Following, Is.True);
            yield return Hold(keyboard.wKey, 20);
            var intents = sent.OfType<NetMoveIntent>().ToList();
            Assert.That(intents, Is.Not.Empty, "W steers while following");
            Assert.That(intents.All(i => i.UnitId == primary), Is.True, "only the primary is steered");
            Assert.That(intents.Any(i => i.Direction.sqrMagnitude > 0.5f), Is.True);
            var state = NetClient.VisibleStates().Single(s => s.UnitId == primary);
            Assert.That(Vector3.Distance(Flat(cameraRig.transform.position), Flat(state.transform.position)), Is.LessThan(0.5f),
                "the followed camera stays on the operative");

            // Esc: the intent is released (zero) before anything else, then W pans again and sends nothing.
            Press(keyboard.wKey);
            yield return null;
            yield return null;
            sent.Clear();
            yield return Tap(keyboard.escapeKey);
            Assert.That(controls.Following, Is.False);
            Assert.That(sent.OfType<NetMoveIntent>().LastOrDefault()?.Direction, Is.EqualTo(Vector3.zero), "leaving follow releases the intent");
            sent.Clear();
            before = Flat(cameraRig.transform.position);
            for (var i = 0; i < 20; i++)
                yield return null;
            Release(keyboard.wKey);
            yield return null;
            Assert.That(sent, Is.Empty, "after follow, W sends nothing");
            Assert.That(Vector3.Distance(Flat(cameraRig.transform.position), before), Is.GreaterThan(0.2f), "and pans the camera again");
        }
    }
}
#endif
