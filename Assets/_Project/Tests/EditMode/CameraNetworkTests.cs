using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CameraNetworkTests
    {
        static CameraSpec Spec(int id = 1) => new CameraSpec(id, new Vector3(8.9f, 2.6f, -1.5f), Vector3.left, 12f, 45f);

        [Test]
        public void ANewNetwork_IsNotCompromised_UntilCompromiseIsCalled()
        {
            var network = new CameraNetwork(new[] { Spec() });
            Assert.That(network.Compromised, Is.False);
            network.Compromise();
            Assert.That(network.Compromised, Is.True);
            network.Compromise();
            Assert.That(network.Compromised, Is.True, "idempotent");
            Assert.That(network.Cameras.Count, Is.EqualTo(1));
        }

        [Test]
        public void Covers_IsTheConeInFrontOfTheCamera()
        {
            var camera = Spec();
            Assert.That(camera.Covers(new Vector3(4f, 1f, -1.5f)), Is.True);
            Assert.That(camera.Covers(new Vector3(4f, 1f, -8.5f)), Is.False, "too far off axis");
            Assert.That(camera.Covers(new Vector3(-6f, 1f, -1.5f)), Is.False, "out of range");
            Assert.That(camera.Covers(new Vector3(12f, 1f, -1.5f)), Is.False, "behind the camera");
        }

        [Test]
        public void ACameraWithoutAVisual_IsFine()
        {
            Assert.That(Spec().Visual == null, Is.True);
        }
    }
}
