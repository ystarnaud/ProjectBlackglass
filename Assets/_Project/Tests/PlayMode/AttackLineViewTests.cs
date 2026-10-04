using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AttackLineViewTests
    {
        TestWorld world;
        CommandableUnit unit;
        Health dummy;
        AttackLineView view;
        LineRenderer line;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            unit = world.CreateUnit(new Vector3(0f, 0f, 0f));
            dummy = world.CreateDummy(new Vector3(0f, 0f, 1.5f));
            // The line lives on a child so a unit's own LineRenderer (the queue view) stays free.
            var lineObject = new GameObject("AttackLine");
            lineObject.transform.SetParent(unit.transform, false);
            line = lineObject.AddComponent<LineRenderer>();
            unit.gameObject.SetActive(false);
            view = unit.gameObject.AddComponent<AttackLineView>();
            view.Initialize(line);
            unit.gameObject.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator StartsHidden()
        {
            yield return null;
            Assert.That(view.IsShowing, Is.False);
            Assert.That(line.enabled, Is.False);
        }

        [UnityTest]
        public IEnumerator Hit_ShowsALineFromAttackerToTarget_ThenHidesIt()
        {
            yield return null;
            unit.Issue(new AttackCommand(dummy));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);

            Assert.That(view.IsShowing, Is.True, "The line must show on a hit");
            Assert.That(line.positionCount, Is.EqualTo(2));
            Assert.That(Vector3.Distance(line.GetPosition(0), unit.transform.position + Vector3.up * 0.5f), Is.LessThan(0.01f));
            Assert.That(Vector3.Distance(line.GetPosition(1), dummy.transform.position + Vector3.up * 0.5f), Is.LessThan(0.01f));

            yield return new WaitForSeconds(0.3f);
            Assert.That(view.IsShowing, Is.False, "The line must hide after its duration");
        }

        [UnityTest]
        public IEnumerator Line_StaysWhilePaused_AndHidesAfterResume()
        {
            var pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            yield return null;
            unit.Issue(new AttackCommand(dummy));
            yield return TestWorld.WaitUntil(() => dummy.Current < dummy.Max, 2f);
            Assert.That(view.IsShowing, Is.True, "Precondition: the line is showing");

            pause.Pause();
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(view.IsShowing, Is.True, "The line runs on simulation time and must freeze while paused");

            pause.Resume();
            yield return new WaitForSeconds(0.3f);
            Assert.That(view.IsShowing, Is.False);
        }
    }
}
