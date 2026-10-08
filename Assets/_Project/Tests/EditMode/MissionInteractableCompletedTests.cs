using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class MissionInteractableCompletedTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
                if (host != null)
                    Object.DestroyImmediate(host);
            hosts.Clear();
        }

        MissionInteractable Terminal(float duration)
        {
            var host = new GameObject("Terminal");
            hosts.Add(host);
            var terminal = host.AddComponent<MissionInteractable>();
            terminal.Initialize(1.8f, duration);
            terminal.SetAvailable(true);
            return terminal;
        }

        CommandableUnit Unit()
        {
            var host = new GameObject("Unit");
            hosts.Add(host);
            host.AddComponent<Health>();
            host.AddComponent<UnitInteractor>();
            return host.AddComponent<CommandableUnit>();
        }

        [Test]
        public void Completed_FiresOnce_WhenTheWorkFinishes()
        {
            var terminal = Terminal(2f);
            var unit = Unit();
            var count = 0;
            MissionInteractable from = null;
            terminal.Completed += item =>
            {
                count++;
                from = item;
            };
            terminal.TryBegin(unit);
            terminal.Advance(unit, 1f);
            Assert.That(count, Is.EqualTo(0));
            terminal.Advance(unit, 1.5f);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(from, Is.SameAs(terminal));
            terminal.Advance(unit, 1f);
            Assert.That(count, Is.EqualTo(1), "a completed terminal is used up and does not fire again");
        }

        [Test]
        public void Completed_DoesNotFire_ForACancelledInteraction()
        {
            var terminal = Terminal(2f);
            var unit = Unit();
            var fired = false;
            terminal.Completed += _ => fired = true;
            terminal.TryBegin(unit);
            terminal.Advance(unit, 1f);
            terminal.Release(unit);
            Assert.That(fired, Is.False);
        }
    }
}
