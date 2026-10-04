using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitMoverSteerPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator Steer_MovesAtUnitSpeed_AndTurnsToFaceTheDirection()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-6f, 0f, 0f));
            var mover = unit.GetComponent<UnitMover>();
            var speed = unit.GetComponent<NavMeshAgent>().speed;
            yield return null;
            var start = unit.transform.position;

            var steeredTime = 0f;
            while (steeredTime < 0.5f)
            {
                mover.Steer(Vector3.right);
                steeredTime += Time.deltaTime;
                yield return null;
            }
            yield return null;

            var travelled = unit.transform.position - start;
            var expected = speed * steeredTime;
            Assert.That(travelled.x, Is.EqualTo(expected).Within(expected * 0.15f));
            Assert.That(Mathf.Abs(travelled.z), Is.LessThan(0.05f));
            Assert.That(Vector3.Dot(unit.transform.forward, Vector3.right), Is.GreaterThan(0.99f), "Did not turn to face the direction");
        }

        [UnityTest]
        public IEnumerator Steer_IntoAWall_StopsAtTheWallAndStaysOnTheNavMesh()
        {
            // A wall from x = 2.5 to 3.5 across the unit's path.
            world.CreateEnvironment((new Vector3(3f, 1f, 0f), new Vector3(1f, 2f, 10f)));
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var mover = unit.GetComponent<UnitMover>();
            var agent = unit.GetComponent<NavMeshAgent>();
            yield return null;

            var steeredTime = 0f;
            while (steeredTime < 1.5f)
            {
                mover.Steer(Vector3.right);
                steeredTime += Time.deltaTime;
                yield return null;
            }

            Assert.That(unit.transform.position.x, Is.GreaterThan(1f), "Did not move toward the wall");
            Assert.That(unit.transform.position.x, Is.LessThan(2.5f), "Walked into the wall");
            Assert.That(agent.isOnNavMesh, Is.True);
        }

        [UnityTest]
        public IEnumerator Steer_WhilePaused_DoesNothing()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var mover = unit.GetComponent<UnitMover>();
            yield return null;
            var start = unit.transform.position;
            var startRotation = unit.transform.rotation;

            pause.Pause();
            for (var i = 0; i < 10; i++)
            {
                mover.Steer(Vector3.right);
                yield return null;
            }

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.001f));
            Assert.That(Quaternion.Angle(unit.transform.rotation, startRotation), Is.LessThan(0.01f));
        }

        [UnityTest]
        public IEnumerator Steer_DuringAMove_DropsThePathAndLeftoverVelocity()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            var mover = unit.GetComponent<UnitMover>();
            var agent = unit.GetComponent<NavMeshAgent>();
            yield return null;
            Assert.That(mover.MoveTo(new Vector3(0f, 0f, 8f)), Is.True);
            yield return new WaitForSeconds(0.4f);
            Assert.That(agent.velocity.magnitude, Is.GreaterThan(1f), "Precondition: the unit should be walking");
            var start = unit.transform.position;

            var steeredTime = 0f;
            while (steeredTime < 0.4f)
            {
                mover.Steer(Vector3.right);
                steeredTime += Time.deltaTime;
                yield return null;
            }

            Assert.That(agent.hasPath, Is.False, "The old path is still active");
            Assert.That(agent.velocity.magnitude, Is.LessThan(0.01f), "Leftover velocity is still applied");
            var travelled = unit.transform.position - start;
            Assert.That(travelled.x, Is.GreaterThan(1f));
            Assert.That(Mathf.Abs(travelled.z), Is.LessThan(0.3f), "Kept drifting along the old path");
        }
    }
}
