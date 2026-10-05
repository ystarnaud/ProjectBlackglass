using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverPointTests
    {
        GameObject host;
        GameObject obstacleHost;
        CoverPoint point;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Cover");
            point = host.AddComponent<CoverPoint>();
            obstacleHost = GameObject.CreatePrimitive(PrimitiveType.Cube);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(obstacleHost);
        }

        [Test]
        public void Forward_IsFlatAndNormalised()
        {
            host.transform.rotation = Quaternion.LookRotation(new Vector3(3f, 4f, 4f));
            Assert.That(point.Forward.y, Is.EqualTo(0f));
            Assert.That(point.Forward.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(point.Forward.x, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(point.Forward.z, Is.EqualTo(0.8f).Within(1e-4f));
        }

        [Test]
        public void Initialize_SetsTheObstacleAndHitChance_DefaultIsHalf()
        {
            Assert.That(point.HitChance, Is.EqualTo(0.5f));
            Assert.That(point.Obstacle, Is.Null);
            var collider = obstacleHost.GetComponent<Collider>();
            point.Initialize(collider, 0.25f);
            Assert.That(point.Obstacle, Is.SameAs(collider));
            Assert.That(point.HitChance, Is.EqualTo(0.25f));
            Assert.That(point.Position, Is.EqualTo(host.transform.position));
        }

        [Test]
        public void ProtectsFrom_WithoutAnObstacle_IsFalse_AndWarnsOnce()
        {
            LogAssert.Expect(LogType.Warning, new Regex("no obstacle"));
            Assert.That(point.ProtectsFrom(new Vector3(0f, 1f, 5f), new Vector3(0f, 1f, 0f)), Is.False);
            Assert.That(point.ProtectsFrom(new Vector3(0f, 1f, 5f), new Vector3(0f, 1f, 0f)), Is.False);
        }

        [Test]
        public void Registry_ListsThePointsItIsGiven()
        {
            var registry = host.AddComponent<CoverRegistry>();
            Assert.That(registry.Points, Is.Empty);
            registry.Initialize(point);
            Assert.That(registry.Points, Is.EqualTo(new[] { point }));
        }
    }
}
