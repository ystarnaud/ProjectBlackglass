using System;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CommandQueueTests
    {
        CommandQueue queue;
        MoveCommand first;
        MoveCommand second;
        MoveCommand third;

        [SetUp]
        public void SetUp()
        {
            queue = new CommandQueue();
            first = new MoveCommand(new Vector3(1f, 0f, 0f));
            second = new MoveCommand(new Vector3(2f, 0f, 0f));
            third = new MoveCommand(new Vector3(3f, 0f, 0f));
        }

        [Test]
        public void StartsIdle()
        {
            Assert.That(queue.Current, Is.Null);
            Assert.That(queue.Pending, Is.Empty);
        }

        [Test]
        public void Append_WhileIdle_BecomesCurrent()
        {
            Assert.That(queue.Append(first), Is.True);
            Assert.That(queue.Current, Is.SameAs(first));
            Assert.That(queue.Pending, Is.Empty);
        }

        [Test]
        public void Append_WhileBusy_WaitsInOrder()
        {
            queue.Append(first);
            Assert.That(queue.Append(second), Is.False);
            Assert.That(queue.Append(third), Is.False);
            Assert.That(queue.Current, Is.SameAs(first));
            Assert.That(queue.Pending, Is.EqualTo(new UnitCommand[] { second, third }));
        }

        [Test]
        public void Replace_DropsCurrentAndPending()
        {
            queue.Append(first);
            queue.Append(second);
            queue.Replace(third);
            Assert.That(queue.Current, Is.SameAs(third));
            Assert.That(queue.Pending, Is.Empty);
        }

        [Test]
        public void Advance_PromotesPendingInOrder_ThenGoesIdle()
        {
            queue.Append(first);
            queue.Append(second);
            queue.Append(third);

            Assert.That(queue.Advance(), Is.SameAs(second));
            Assert.That(queue.Pending, Is.EqualTo(new UnitCommand[] { third }));
            Assert.That(queue.Advance(), Is.SameAs(third));
            Assert.That(queue.Pending, Is.Empty);
            Assert.That(queue.Advance(), Is.Null);
            Assert.That(queue.Current, Is.Null);
        }

        [Test]
        public void Advance_WhenIdle_StaysIdle()
        {
            Assert.That(queue.Advance(), Is.Null);
            Assert.That(queue.Current, Is.Null);
        }

        [Test]
        public void Clear_EmptiesEverything()
        {
            queue.Append(first);
            queue.Append(second);
            queue.Clear();
            Assert.That(queue.Current, Is.Null);
            Assert.That(queue.Pending, Is.Empty);
        }

        [Test]
        public void NullCommands_AreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => queue.Replace(null));
            Assert.Throws<ArgumentNullException>(() => queue.Append(null));
            Assert.That(queue.Current, Is.Null);
        }

        [Test]
        public void StopCommand_IsAUnitCommand()
        {
            Assert.That(new StopCommand(), Is.InstanceOf<UnitCommand>());
        }
    }
}
