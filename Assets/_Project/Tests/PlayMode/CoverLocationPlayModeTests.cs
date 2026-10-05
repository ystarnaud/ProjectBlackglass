using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CoverLocationPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        static Vector3 Pivot(float x, float z) => new Vector3(x, 1f, z);

        [UnityTest]
        public IEnumerator OverAWaistHighWall_ProtectsStraightOn_FromNearAndFar_ButNotFromBehind()
        {
            // A 0.9 m wall from z -0.25 to 0.25; the stand point is 0.75 m south of its face. With the 1.5 m eye and
            // the ray ending at the feet, the ray meets the near face at 1.125 / D: 0.56 m from 2 m, 0.14 m from 8 m.
            var environment = world.CreateEnvironment((new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            yield return new WaitForFixedUpdate();
            var defender = Pivot(0f, -1f);

            Assert.That(point.ProtectsFrom(Pivot(0f, 1f), defender), Is.True, "2 m away over the wall");
            Assert.That(point.ProtectsFrom(Pivot(0f, 7f), defender), Is.True, "8 m away over the wall");
            Assert.That(point.ProtectsFrom(Pivot(0f, -5f), defender), Is.False, "From behind the defender the wall is not between them");
        }

        [UnityTest]
        public IEnumerator BesideAPillar_TheFlankIsOpen_PastTheCorner()
        {
            // A 1.5 m pillar centred at the origin (x and z -0.75..0.75), the point 0.75 m south of its face. The
            // near corner lies at exactly 45 deg off the point's forward, so the tests sit on either side of it:
            // 30 deg (the ray crosses the face at x 0.43) and 55 deg (the ray passes the corner about 0.2 m clear).
            var environment = world.CreateEnvironment((new Vector3(0f, 1.5f, 0f), new Vector3(1.5f, 3f, 1.5f)));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1.5f), Vector3.forward, TestWorld.ObstacleCollider(environment));
            yield return new WaitForFixedUpdate();
            var defender = Pivot(0f, -1.5f);
            var at30 = Pivot(4f * Mathf.Sin(30f * Mathf.Deg2Rad), -1.5f + 4f * Mathf.Cos(30f * Mathf.Deg2Rad));
            var at55 = Pivot(4f * Mathf.Sin(55f * Mathf.Deg2Rad), -1.5f + 4f * Mathf.Cos(55f * Mathf.Deg2Rad));

            Assert.That(point.ProtectsFrom(at30, defender), Is.True, "30 deg: the pillar is between");
            Assert.That(point.ProtectsFrom(at55, defender), Is.False, "55 deg: the ray clears the corner, the flank is open");
        }

        [UnityTest]
        public IEnumerator OnlyTheWiredObstacleCounts()
        {
            // Two walls: the point is wired to the far one, and the near one (which the ray crosses) must not count.
            var environment = world.CreateEnvironment(
                (new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f)),
                (new Vector3(0f, 0.45f, 4f), new Vector3(4f, 0.9f, 0.5f)));
            var wiredToFarWall = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, TestWorld.ObstacleCollider(environment, 1));
            yield return new WaitForFixedUpdate();

            Assert.That(wiredToFarWall.ProtectsFrom(Pivot(0f, 2f), Pivot(0f, -1f)), Is.False,
                "The attacker stands between the two walls: the wired (far) wall is not between it and the defender");
        }

        [UnityTest]
        public IEnumerator ALooseObstacle_WorksLikeASceneOne()
        {
            world.CreateEnvironment();
            var box = world.CreateObstacle(new Vector3(0f, 0.45f, 0f), new Vector3(4f, 0.9f, 0.5f));
            var point = world.CreateCoverPoint(new Vector3(0f, 0f, -1f), Vector3.forward, box.GetComponent<Collider>());
            yield return new WaitForFixedUpdate();

            Assert.That(point.ProtectsFrom(Pivot(0f, 5f), Pivot(0f, -1f)), Is.True);
            Assert.That(point.Obstacle, Is.SameAs(box.GetComponent<Collider>()));
        }
    }
}
