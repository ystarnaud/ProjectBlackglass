using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// Owner ruling (Stage 12 Track L final rulings §1): outcomeSequence is the sequence number of the first committed terminal
    /// gameplay event. The counter starts at zero, counts committed events only, the terminal event takes the next number, the
    /// freeze keeps it, and an overflow fails closed (never wraps or clamps).
    /// </summary>
    public sealed class OutcomeSequenceTests
    {
        [Test]
        public void The_counter_starts_at_zero_and_the_terminal_event_takes_the_next_number()
        {
            var sequence = new OutcomeSequence();
            Assert.That(sequence.Committed, Is.EqualTo(0));
            Assert.That(sequence.TryFreeze(out var terminal), Is.True);
            Assert.That(terminal, Is.EqualTo(1), "a terminal event with nothing before it is event 1");

            sequence = new OutcomeSequence();
            sequence.Commit();
            sequence.Commit();
            sequence.Commit();
            Assert.That(sequence.Committed, Is.EqualTo(3));
            Assert.That(sequence.TryFreeze(out terminal), Is.True);
            Assert.That(terminal, Is.EqualTo(4));
        }

        [Test]
        public void The_freeze_keeps_its_number_and_later_events_change_nothing()
        {
            var sequence = new OutcomeSequence();
            sequence.Commit();
            Assert.That(sequence.TryFreeze(out var first), Is.True);
            sequence.Commit();
            sequence.Commit();
            Assert.That(sequence.TryFreeze(out var second), Is.True, "a repeated freeze answers the same");
            Assert.That((first, second, sequence.Committed, sequence.Terminal), Is.EqualTo((2, 2, 1, 2)));
            Assert.That(sequence.IsFrozen, Is.True);
        }

        [Test]
        public void An_overflow_fails_closed_instead_of_wrapping_or_clamping()
        {
            var sequence = new OutcomeSequence(maximum: 3);
            sequence.Commit();
            sequence.Commit();
            Assert.That(sequence.TryFreeze(out var terminal), Is.True, "events 1-2, terminal 3: inside the bound");
            Assert.That(terminal, Is.EqualTo(3));

            sequence = new OutcomeSequence(maximum: 3);
            for (var i = 0; i < 3; i++) sequence.Commit();
            Assert.That(sequence.Overflowed, Is.True, "the terminal event could no longer be numbered");
            Assert.That(sequence.TryFreeze(out terminal), Is.False);
            Assert.That(terminal, Is.EqualTo(0));
            Assert.That(sequence.IsFrozen, Is.True, "an overflowed sequence is frozen as failed: it never recovers");
            for (var i = 0; i < 10; i++) sequence.Commit();
            Assert.That(sequence.Committed, Is.EqualTo(3), "no wrap");
            Assert.That(sequence.TryFreeze(out _), Is.False, "still failed");
        }

        [Test]
        public void The_default_bound_is_the_manifests()
        {
            Assert.That(new OutcomeSequence().Maximum, Is.EqualTo(int.MaxValue));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new OutcomeSequence(maximum: 0));
        }
    }
}
