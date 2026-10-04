using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class SimulationTimeTests
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
        public IEnumerator IsRunning_WhileSimulationAdvances()
        {
            yield return null;
            Assert.That(SimulationTime.IsRunning, Is.True);
        }

        [UnityTest]
        public IEnumerator IsRunning_FalseOnThePauseFrame_WhilePaused_AndTrueAgainAfterResume()
        {
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            yield return null;

            pause.Pause();
            Assert.That(Time.deltaTime, Is.GreaterThan(0f), "Precondition: deltaTime still holds this frame's value on the pause frame");
            Assert.That(SimulationTime.IsRunning, Is.False, "Simulation must not run on the frame it was paused");

            yield return null;
            Assert.That(SimulationTime.IsRunning, Is.False, "Simulation must not run while paused");

            pause.Resume();
            yield return null;
            yield return null;
            Assert.That(SimulationTime.IsRunning, Is.True, "Simulation must run again after resume");
        }

        // Runs before every unit's Update in the same frame, like an input callback that pauses the game: on that
        // frame Time.deltaTime is still positive while Time.timeScale is already zero.
        [DefaultExecutionOrder(-1000)]
        sealed class EarlyUpdatePauser : MonoBehaviour
        {
            public TacticalPause Pause;
            public CommandableUnit Unit;
            public Health Target;
            public bool Fire;

            void Update()
            {
                if (!Fire)
                    return;
                Fire = false;
                Unit.Issue(new AttackCommand(Target));
                Pause.Pause();
            }
        }

        [UnityTest]
        public IEnumerator AttackIssuedAndPausedInTheSameEarlyUpdate_DealsNoDamageUntilResume()
        {
            world.CreateEnvironment();
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            var unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            var dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            var pauser = world.Track(new GameObject("Pauser")).AddComponent<EarlyUpdatePauser>();
            pauser.Pause = pause;
            pauser.Unit = unit;
            pauser.Target = dummy;
            yield return null;

            pauser.Fire = true;
            yield return null;   // the pauser issues the attack and pauses before the unit's Update runs
            yield return null;

            Assert.That(unit.CurrentCommand, Is.TypeOf<AttackCommand>(), "The order must be accepted while paused");
            Assert.That(dummy.Current, Is.EqualTo(dummy.Max), "Damage landed on the pause frame");

            pause.Resume();
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);
            Assert.That(dummy.Current, Is.EqualTo(75), "The hit did not land after resume");
        }
    }
}
