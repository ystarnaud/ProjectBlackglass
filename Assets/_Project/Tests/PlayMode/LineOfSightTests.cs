using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class LineOfSightTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();

        [UnityTest]
        public IEnumerator IsClear_UnitsNeverBlock_WallsDo()
        {
            world.CreateEnvironment((new Vector3(5f, 1f, 0f), new Vector3(1f, 2f, 6f)));
            var seen = world.CreateFighter(new Vector3(0f, 0f, -4f));
            var blocker = world.CreateFighter(new Vector3(0f, 0f, -2f));
            var behindWall = world.CreateFighter(new Vector3(8f, 0f, 0f));
            yield return new WaitForFixedUpdate();   // colliders take their positions
            var buffer = new RaycastHit[LineOfSight.HitBufferSize];
            var eye = new Vector3(0f, 1.5f, 1f);

            Assert.That(LineOfSight.IsClear(eye, HealthOf(seen).transform.position, ~0, buffer), Is.True, "A unit in between must not block sight");
            Assert.That(LineOfSight.IsClear(eye, HealthOf(blocker).transform.position, ~0, buffer), Is.True);
            Assert.That(LineOfSight.IsClear(eye, HealthOf(behindWall).transform.position, ~0, buffer), Is.False, "A wall must block sight");
        }

        [UnityTest]
        public IEnumerator IsClear_FromAPivot_LooksFromTheEyeHeight_SoALowCrateDoesNotBlock()
        {
            // A 1 m crate halfway: the line from the eye (1.5 m) to the target's pivot (1 m) passes 1.25 m up there.
            // A 2 m wall at the same spot blocks it.
            world.CreateEnvironment((new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f)), (new Vector3(6f, 1f, 0f), new Vector3(1f, 2f, 1f)));
            var overCrate = world.CreateFighter(new Vector3(0f, 0f, 4f));
            var behindWall = world.CreateFighter(new Vector3(6f, 0f, 4f));
            yield return new WaitForFixedUpdate();
            var buffer = new RaycastHit[LineOfSight.HitBufferSize];

            Assert.That(LineOfSight.IsClear(new Vector3(0f, 1f, -4f), HealthOf(overCrate), ~0, buffer), Is.True, "A 1 m crate must not block a 1.5 m eye");
            Assert.That(LineOfSight.IsClear(new Vector3(6f, 1f, -4f), HealthOf(behindWall), ~0, buffer), Is.False, "A 2 m wall must block");
            Assert.That(LineOfSight.EyeHeight, Is.EqualTo(0.5f));
        }

        [UnityTest]
        public IEnumerator UnitAttacker_HasLineOfSight_FromItsOwnPivot_AndFromAnotherPoint()
        {
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(3f, 2f, 1f)));
            var attacker = world.CreateFighter(new Vector3(0f, 0f, -4f));
            var target = world.CreateFighter(new Vector3(0f, 0f, 4f));
            yield return new WaitForFixedUpdate();
            var unitAttacker = attacker.GetComponent<UnitAttacker>();

            Assert.That(unitAttacker.HasLineOfSight(HealthOf(target)), Is.False, "The wall is between them");
            Assert.That(unitAttacker.HasLineOfSightFrom(new Vector3(4f, 1f, -4f), HealthOf(target)), Is.True, "From 4 m to the side the wall is cleared");
            Assert.That(unitAttacker.HasLineOfSightFrom(new Vector3(0f, 1f, -8f), HealthOf(target)), Is.False);
        }
    }
}
