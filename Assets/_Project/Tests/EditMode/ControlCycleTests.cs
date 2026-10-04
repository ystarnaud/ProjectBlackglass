using System;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class ControlCycleTests
    {
        static bool All(int index) => true;

        [TestCase(3, 0, 1, 1)]
        [TestCase(3, 2, 1, 0)]
        [TestCase(3, 0, -1, 2)]
        [TestCase(3, 1, -1, 0)]
        [TestCase(4, 3, 1, 0)]
        [TestCase(4, 0, -1, 3)]
        public void NextIndex_AllEligible_StepsAndWraps(int count, int current, int direction, int expected)
        {
            Assert.That(ControlCycle.NextIndex(count, current, direction, All), Is.EqualTo(expected));
        }

        // Entries 0 and 2 of 4 are eligible.
        [TestCase(0, 1, 2)]
        [TestCase(2, 1, 0)]
        [TestCase(0, -1, 2)]
        [TestCase(2, -1, 0)]
        public void NextIndex_SkipsIneligibleEntries(int current, int direction, int expected)
        {
            Assert.That(ControlCycle.NextIndex(4, current, direction, i => i == 0 || i == 2), Is.EqualTo(expected));
        }

        [Test]
        public void NextIndex_OnlyCurrentEligible_ReturnsCurrent()
        {
            Assert.That(ControlCycle.NextIndex(3, 1, 1, i => i == 1), Is.EqualTo(1));
            Assert.That(ControlCycle.NextIndex(3, 1, -1, i => i == 1), Is.EqualTo(1));
        }

        [Test]
        public void NextIndex_NothingEligible_ReturnsMinusOne()
        {
            Assert.That(ControlCycle.NextIndex(3, 0, 1, i => false), Is.EqualTo(-1));
        }

        [Test]
        public void NextIndex_EmptyList_ReturnsMinusOne()
        {
            Assert.That(ControlCycle.NextIndex(0, -1, 1, All), Is.EqualTo(-1));
            Assert.That(ControlCycle.NextIndex(0, -1, -1, All), Is.EqualTo(-1));
        }

        [TestCase(-1)]
        [TestCase(3)]
        public void NextIndex_CurrentOutsideTheList_Forward_StartsAtTheFirstEligible(int current)
        {
            Assert.That(ControlCycle.NextIndex(3, current, 1, All), Is.EqualTo(0));
        }

        [TestCase(-1)]
        [TestCase(3)]
        public void NextIndex_CurrentOutsideTheList_Backward_StartsAtTheLastEligible(int current)
        {
            Assert.That(ControlCycle.NextIndex(3, current, -1, All), Is.EqualTo(2));
        }

        [Test]
        public void NextIndex_IneligibleCurrent_MovesToAnotherEligibleEntry()
        {
            Assert.That(ControlCycle.NextIndex(3, 0, 1, i => i == 2), Is.EqualTo(2));
            Assert.That(ControlCycle.NextIndex(3, 0, -1, i => i == 2), Is.EqualTo(2));
        }

        [Test]
        public void NextIndex_IneligibleCurrent_AndNothingElse_ReturnsMinusOne()
        {
            Assert.That(ControlCycle.NextIndex(1, 0, 1, i => false), Is.EqualTo(-1));
        }

        [TestCase(0)]
        [TestCase(2)]
        public void NextIndex_DirectionOtherThanPlusOrMinusOne_Throws(int direction)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ControlCycle.NextIndex(3, 0, direction, All));
        }

        [Test]
        public void NextIndex_NegativeCount_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ControlCycle.NextIndex(-1, 0, 1, All));
        }

        [Test]
        public void NextIndex_NullPredicate_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ControlCycle.NextIndex(3, 0, 1, null));
        }
    }
}
