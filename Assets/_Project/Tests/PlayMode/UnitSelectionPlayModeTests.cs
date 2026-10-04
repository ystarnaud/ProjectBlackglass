using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitSelectionPlayModeTests
    {
        TestWorld world;
        UnitSelection selection;
        SelectableUnit a;
        SelectableUnit b;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment();
            a = world.CreateFriendly(new Vector3(-2f, 0f, 0f));
            b = world.CreateFriendly(new Vector3(2f, 0f, 0f));
            selection = world.Track(new GameObject("Selection")).AddComponent<UnitSelection>();
            selection.Initialize(a, b);
        }

        [TearDown]
        public void TearDown() => world.Dispose();

        [UnityTest]
        public IEnumerator DisabledUnit_IsDroppedFromTheSelection()
        {
            yield return null;
            selection.SetSelection(new[] { a, b });
            var changes = 0;
            selection.Changed += () => changes++;

            a.gameObject.SetActive(false);

            Assert.That(selection.Selected, Is.EqualTo(new[] { b }));
            Assert.That(a.IsSelected, Is.False);
            Assert.That(changes, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DestroyedUnit_IsDroppedFromTheSelection()
        {
            yield return null;
            selection.SetSelection(new[] { a, b });

            Object.Destroy(a.gameObject);
            yield return null;

            Assert.That(selection.Selected, Is.EqualTo(new[] { b }));
            Assert.That(GroupOrders.Issue(new[] { selection.Selected[0].Unit }, new StopCommand(), IssueMode.Replace), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Indicator_ShowsTheRingOnlyWhileSelected()
        {
            var ring = new GameObject("Ring");
            ring.transform.SetParent(a.transform, false);
            var indicator = a.gameObject.AddComponent<SelectionIndicator>();
            indicator.Initialize(ring);
            yield return null;
            Assert.That(ring.activeSelf, Is.False);

            selection.Select(a);
            Assert.That(ring.activeSelf, Is.True);

            selection.Select(b);
            Assert.That(ring.activeSelf, Is.False);
        }
    }
}
