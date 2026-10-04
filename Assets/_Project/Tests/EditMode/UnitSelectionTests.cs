using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class UnitSelectionTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();
        UnitSelection selection;
        SelectableUnit a;
        SelectableUnit b;
        SelectableUnit c;
        int changes;

        [SetUp]
        public void SetUp()
        {
            a = CreateSelectable("A");
            b = CreateSelectable("B");
            c = CreateSelectable("C");
            var host = new GameObject("Selection");
            hosts.Add(host);
            selection = host.AddComponent<UnitSelection>();
            selection.Initialize(a, b, c);
            changes = 0;
            selection.Changed += () => changes++;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
                Object.DestroyImmediate(host);
            hosts.Clear();
        }

        SelectableUnit CreateSelectable(string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            return host.AddComponent<SelectableUnit>();
        }

        [Test]
        public void StartsEmpty_WithTheRoster()
        {
            Assert.That(selection.Selected, Is.Empty);
            Assert.That(selection.Roster, Is.EqualTo(new[] { a, b, c }));
        }

        [Test]
        public void SelectableUnit_ExposesItsUnit()
        {
            Assert.That(a.Unit, Is.SameAs(a.GetComponent<CommandableUnit>()));
        }

        [Test]
        public void Select_SelectsOnlyThatUnit()
        {
            selection.Select(a);
            selection.Select(b);
            Assert.That(selection.Selected, Is.EqualTo(new[] { b }));
            Assert.That(a.IsSelected, Is.False);
            Assert.That(b.IsSelected, Is.True);
            Assert.That(changes, Is.EqualTo(2));
        }

        [Test]
        public void Select_SameUnitTwice_RaisesChangedOnce()
        {
            selection.Select(a);
            selection.Select(a);
            Assert.That(changes, Is.EqualTo(1));
        }

        [Test]
        public void Toggle_AddsThenRemoves()
        {
            selection.Select(a);
            selection.Toggle(b);
            Assert.That(selection.Selected, Is.EqualTo(new[] { a, b }));
            selection.Toggle(a);
            Assert.That(selection.Selected, Is.EqualTo(new[] { b }));
            Assert.That(a.IsSelected, Is.False);
            Assert.That(changes, Is.EqualTo(3));
        }

        [Test]
        public void Add_KeepsExistingAndSkipsDuplicates()
        {
            selection.Select(b);
            selection.Add(new[] { a, b, c });
            Assert.That(selection.Selected, Is.EqualTo(new[] { b, a, c }));
            Assert.That(changes, Is.EqualTo(2));
            selection.Add(new[] { a });
            Assert.That(changes, Is.EqualTo(2), "Adding only already-selected units is not a change");
        }

        [Test]
        public void SetSelection_ReplacesInTheGivenOrder()
        {
            selection.Select(a);
            selection.SetSelection(new[] { c, b });
            Assert.That(selection.Selected, Is.EqualTo(new[] { c, b }));
            Assert.That(a.IsSelected, Is.False);
            Assert.That(b.IsSelected && c.IsSelected, Is.True);
        }

        [Test]
        public void SetSelection_SkipsNullEntries()
        {
            selection.SetSelection(new[] { a, null, b });
            Assert.That(selection.Selected, Is.EqualTo(new[] { a, b }));
        }

        [Test]
        public void Clear_DeselectsEverything()
        {
            selection.SetSelection(new[] { a, b });
            selection.Clear();
            Assert.That(selection.Selected, Is.Empty);
            Assert.That(a.IsSelected || b.IsSelected, Is.False);
            selection.Clear();
            Assert.That(changes, Is.EqualTo(2), "Clearing an empty selection is not a change");
        }

        [Test]
        public void InactiveUnit_CannotBeSelected()
        {
            c.gameObject.SetActive(false);
            selection.Select(c);
            selection.Toggle(c);
            selection.Add(new[] { c });
            Assert.That(selection.Selected, Is.Empty);
            Assert.That(changes, Is.EqualTo(0));
        }

        [Test]
        public void IsSelected_RaisesSelectionChangedOnlyOnChange()
        {
            var events = new List<bool>();
            a.SelectionChanged += events.Add;
            selection.Select(a);
            selection.Select(a);
            selection.Clear();
            Assert.That(events, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void AddToRoster_AddsOnce()
        {
            var d = CreateSelectable("D");
            selection.AddToRoster(d);
            selection.AddToRoster(d);
            Assert.That(selection.Roster, Is.EqualTo(new[] { a, b, c, d }));
        }

        [Test]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => selection.Select(null));
            Assert.Throws<ArgumentNullException>(() => selection.Toggle(null));
            Assert.Throws<ArgumentNullException>(() => selection.Add(null));
            Assert.Throws<ArgumentNullException>(() => selection.SetSelection(null));
            Assert.Throws<ArgumentNullException>(() => selection.AddToRoster(null));
        }
    }
}
