using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class RangedCombatPlayModeTests
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

        static Health HealthOf(Component unit) => unit.GetComponent<Health>();

        [UnityTest]
        public IEnumerator RangedTryAttack_BehindAWall_DoesNotHit_AndHitsOnceTheWallIsCleared()
        {
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(3f, 2f, 1f)));
            var ranged = world.CreateFighter(new Vector3(0f, 0f, -3f), role: CombatRole.Ranged, range: 8f);
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 3f));
            yield return new WaitForFixedUpdate();
            var attacker = ranged.GetComponent<UnitAttacker>();

            Assert.That(attacker.IsInRange(dummy), Is.True, "Precondition: 6 m is inside the 8 m range");
            Assert.That(attacker.CanAttack(dummy), Is.False, "The wall blocks sight");
            Assert.That(attacker.TryAttack(dummy), Is.False, "A ranged attack must not land through a wall");
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max));

            // A second ranged unit already standing to the side (teleporting a NavMeshAgent by transform is unreliable).
            var sideAttacker = world.CreateFighter(new Vector3(4f, 0f, -3f), role: CombatRole.Ranged, range: 8f).GetComponent<UnitAttacker>();
            yield return new WaitForFixedUpdate();
            Assert.That(sideAttacker.CanAttack(dummy), Is.True, "From the side the wall is cleared");
            Assert.That(sideAttacker.TryAttack(dummy), Is.True);
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max - sideAttacker.Damage));
        }
    }
}
