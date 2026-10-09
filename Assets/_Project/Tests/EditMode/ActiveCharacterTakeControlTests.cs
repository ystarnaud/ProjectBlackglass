using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ActiveCharacterTakeControlTests
    {
        GameObject systems;
        ActiveCharacter active;
        UnitSelection selection;
        SelectableUnit[] squad;
        readonly List<GameObject> hosts = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            Time.timeScale = 1f;
            systems = new GameObject("Systems");
            var pause = systems.AddComponent<TacticalPause>();
            active = systems.AddComponent<ActiveCharacter>();
            selection = systems.AddComponent<UnitSelection>();
            squad = new[] { CreateFriendly("A"), CreateFriendly("B"), CreateFriendly("C") };
            selection.Initialize(squad);
            active.Initialize(squad[0].Unit, pause, selection);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(systems);
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
            Time.timeScale = 1f;
        }

        SelectableUnit CreateFriendly(string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            return host.AddComponent<SelectableUnit>();
        }

        [Test]
        public void TakingControlOfAnEligibleRosterUnit_MakesItActive_AndSelectsOnlyIt()
        {
            selection.Select(squad[0]);
            selection.Add(new[] { squad[2] });

            Assert.That(active.TakeControl(squad[1].Unit), Is.True);

            Assert.That(active.Unit, Is.SameAs(squad[1].Unit));
            Assert.That(selection.Selected, Is.EqualTo(new[] { squad[1] }));
        }

        [Test]
        public void TakingControlOfADeadUnit_ReturnsFalse_AndChangesNothing()
        {
            selection.Select(squad[0]);
            var health = squad[1].gameObject.AddComponent<Health>();
            health.TakeDamage(health.Max);
            squad[1].gameObject.SetActive(true);
            Assert.That(health.IsAlive, Is.False);

            Assert.That(active.TakeControl(squad[1].Unit), Is.False);

            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
            Assert.That(selection.Selected, Is.EqualTo(new[] { squad[0] }));
        }

        [Test]
        public void TakingControlOfAnInactiveUnit_ReturnsFalse_AndChangesNothing()
        {
            selection.Select(squad[0]);
            squad[1].gameObject.SetActive(false);

            Assert.That(active.TakeControl(squad[1].Unit), Is.False);

            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
            Assert.That(selection.Selected, Is.EqualTo(new[] { squad[0] }));
        }

        [Test]
        public void TakingControlOfAUnitOutsideTheRoster_ReturnsFalse_AndChangesNothing()
        {
            selection.Select(squad[0]);
            var stranger = new GameObject("Stranger");
            hosts.Add(stranger);
            var strangerUnit = stranger.AddComponent<CommandableUnit>();

            Assert.That(active.TakeControl(strangerUnit), Is.False);

            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
            Assert.That(selection.Selected, Is.EqualTo(new[] { squad[0] }));
        }

        [Test]
        public void TakingControlOfTheUnitAlreadyInControl_ReturnsFalse_AndLeavesTheSelection()
        {
            selection.Select(squad[1]);

            Assert.That(active.TakeControl(squad[0].Unit), Is.False);

            Assert.That(active.Unit, Is.SameAs(squad[0].Unit));
            Assert.That(selection.Selected, Is.EqualTo(new[] { squad[1] }));
        }
    }
}
