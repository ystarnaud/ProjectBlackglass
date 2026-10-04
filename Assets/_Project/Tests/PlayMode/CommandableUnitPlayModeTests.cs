using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class CommandableUnitPlayModeTests
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
        public IEnumerator Move_ToFarSideOfWall_ArrivesAndClearsOrder()
        {
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(8f, 2f, 1f)));
            var unit = world.CreateUnit(new Vector3(0f, 0f, -6f));
            yield return null;
            var destination = new Vector3(0f, 0f, 6f);

            Assert.That(unit.Issue(new MoveCommand(destination)), Is.True);
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(unit.CurrentCommand, Is.Null, "Move order did not complete in time");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Issue_MoveOffNavMesh_IsRejectedAndPreviousOrderKept()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(-5f, 0f, -5f));
            yield return null;
            var first = new MoveCommand(new Vector3(5f, 0f, 5f));
            unit.Issue(first);

            LogAssert.Expect(LogType.Warning, new Regex("no walkable NavMesh point"));
            Assert.That(unit.Issue(new MoveCommand(new Vector3(100f, 0f, 100f))), Is.False);

            Assert.That(unit.CurrentCommand, Is.SameAs(first));
        }

        [UnityTest]
        public IEnumerator Attack_OutOfRange_ChasesAndDestroysTarget()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -8f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 8f));
            yield return null;

            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 10f);
            Assert.That(dummy.Current, Is.LessThan(dummy.Max), "Unit never reached and hit the dummy");

            yield return TestWorld.WaitUntil(() => !dummy.IsAlive, 10f);

            Assert.That(dummy.IsAlive, Is.False);
            Assert.That(dummy.gameObject.activeSelf, Is.False);
            Assert.That(unit.CurrentCommand, Is.Null);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, dummy.transform.position), Is.LessThanOrEqualTo(2.5f));
        }

        [UnityTest]
        public IEnumerator Attack_RespectsCooldown()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            yield return null;

            unit.Issue(new AttackCommand(dummy));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);
            Assert.That(dummy.Current, Is.EqualTo(75));

            yield return new WaitForSeconds(0.5f);
            Assert.That(dummy.Current, Is.EqualTo(75), "Second hit landed before the cooldown");

            yield return new WaitForSeconds(0.7f);
            Assert.That(dummy.Current, Is.EqualTo(50));
        }

        [UnityTest]
        public IEnumerator Attack_TargetKilledElsewhere_UnitStopsAndClearsOrder()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -10f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 10f));
            yield return null;
            unit.Issue(new AttackCommand(dummy));
            yield return new WaitForSeconds(0.3f);

            dummy.TakeDamage(1000);
            yield return null;
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
            var stoppedAt = unit.transform.position;
            yield return new WaitForSeconds(0.5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, stoppedAt), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator Attack_ReissuedOnSameTargetMidChase_KeepsClosingDistance()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -10f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 10f));
            yield return null;
            unit.Issue(new AttackCommand(dummy));
            yield return new WaitForSeconds(0.5f);
            var before = TestWorld.HorizontalDistance(unit.transform.position, dummy.transform.position);

            var deadline = Time.time + 0.5f;
            while (Time.time < deadline)
            {
                Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);
                yield return null;
            }

            var after = TestWorld.HorizontalDistance(unit.transform.position, dummy.transform.position);
            Assert.That(after, Is.LessThan(before - 2f), "Unit stuttered while the same attack was re-issued");
            Assert.That(unit.CurrentCommand, Is.TypeOf<AttackCommand>());
        }

        [UnityTest]
        public IEnumerator Issue_AttackOnDeactivatedTarget_IsRejectedAndPreviousOrderKept()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -10f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 10f));
            yield return null;
            var first = new MoveCommand(new Vector3(5f, 0f, -5f));
            unit.Issue(first);
            dummy.gameObject.SetActive(false);

            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.False);

            Assert.That(unit.CurrentCommand, Is.SameAs(first));
        }

        [UnityTest]
        public IEnumerator Attack_TargetDeactivatedWhileAlive_UnitStopsAndClearsOrder()
        {
            world.CreateEnvironment();
            var unit = world.CreateUnit(new Vector3(0f, 0f, -10f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 10f));
            yield return null;
            unit.Issue(new AttackCommand(dummy));
            yield return new WaitForSeconds(0.3f);

            dummy.gameObject.SetActive(false);
            yield return null;
            yield return null;

            Assert.That(dummy.IsAlive, Is.True);
            Assert.That(unit.CurrentCommand, Is.Null);
            var stoppedAt = unit.transform.position;
            yield return new WaitForSeconds(0.5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, stoppedAt), Is.LessThan(0.5f));
        }

        [UnityTest]
        public IEnumerator Paused_MoveIsAcceptedButWaitsUntilResume()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(-5f, 0f, 0f));
            yield return null;
            var start = unit.transform.position;
            var destination = new Vector3(5f, 0f, 0f);

            pause.Pause();
            Assert.That(unit.Issue(new MoveCommand(destination)), Is.True);
            yield return new WaitForSecondsRealtime(1f);

            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f), "Unit moved while paused");
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            pause.Resume();
            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 10f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Paused_AttackInRange_DealsNoDamageUntilResume()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            yield return null;

            pause.Pause();
            unit.Issue(new AttackCommand(dummy));
            yield return new WaitForSecondsRealtime(1f);
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max), "Attack landed while paused");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);
            Assert.That(dummy.Current, Is.EqualTo(75));
        }
    }
}
