using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ActiveCharacterMarkerTests
    {
        TestWorld world;
        ActiveCharacter active;
        ActiveCharacterMarker marker;
        GameObject visual;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            active = world.Track(new GameObject("Systems")).AddComponent<ActiveCharacter>();
            var markerObject = world.Track(new GameObject("ActiveMarker"));
            markerObject.SetActive(false);
            visual = new GameObject("Visual");
            visual.transform.SetParent(markerObject.transform, false);
            marker = markerObject.AddComponent<ActiveCharacterMarker>();
            marker.Initialize(active, visual);
            markerObject.SetActive(true);
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        void AssertAbove(Component unit) =>
            Assert.That(Vector3.Distance(marker.transform.position, unit.transform.position + Vector3.up * 1.6f),
                Is.LessThan(0.01f), "The marker is not above the active character");

        [UnityTest]
        public IEnumerator NoActiveCharacter_HidesTheVisual()
        {
            yield return null;
            Assert.That(visual.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator FloatsAboveTheActiveCharacter()
        {
            var unit = world.CreateUnit(new Vector3(3f, 0f, 4f));
            active.Initialize(unit, null);
            yield return null;

            Assert.That(visual.activeSelf, Is.True);
            AssertAbove(unit);
        }

        [UnityTest]
        public IEnumerator MovesToTheNewCharacterAfterASwitch()
        {
            var first = world.CreateUnit(new Vector3(3f, 0f, 4f));
            var second = world.CreateUnit(new Vector3(-5f, 0f, -2f));
            active.Initialize(first, null);
            yield return null;

            active.SetUnit(second);
            yield return null;
            AssertAbove(second);
        }

        [UnityTest]
        public IEnumerator DeactivatedUnit_HidesTheVisual()
        {
            var unit = world.CreateUnit(new Vector3(3f, 0f, 4f));
            active.Initialize(unit, null);
            yield return null;

            unit.gameObject.SetActive(false);
            yield return null;
            Assert.That(visual.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator DestroyedUnit_HidesTheVisual()
        {
            var unit = world.CreateUnit(new Vector3(3f, 0f, 4f));
            active.Initialize(unit, null);
            yield return null;

            Object.Destroy(unit.gameObject);
            yield return null;
            yield return null;
            Assert.That(visual.activeSelf, Is.False);
            // Any error or exception logged meanwhile fails the test.
        }
    }
}
