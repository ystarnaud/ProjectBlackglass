using NUnit.Framework;

namespace Blackglass.Tests
{
    public class NetBudgetTests
    {
        [Test]
        public void TheBurst_IsAllowed_ThenRequestsAreDropped()
        {
            var budget = new NetBudget(requestsPerSecond: 10, burst: 5, abuseLimit: 100, abuseWindowSeconds: 10);
            for (var i = 0; i < 5; i++)
                Assert.That(budget.TryTake(0d), Is.True, "request " + i);
            Assert.That(budget.TryTake(0d), Is.False);
        }

        [Test]
        public void Tokens_RefillWithTime()
        {
            var budget = new NetBudget(10, 5, 100, 10);
            for (var i = 0; i < 5; i++)
                budget.TryTake(0d);
            for (var i = 0; i < 3; i++)
                Assert.That(budget.TryTake(0.3d), Is.True, "refilled " + i);
            Assert.That(budget.TryTake(0.3d), Is.False);
        }

        [Test]
        public void TheBucketNeverExceedsTheBurst()
        {
            var budget = new NetBudget(10, 5, 100, 10);
            budget.TryTake(0d);
            for (var i = 0; i < 5; i++)
                Assert.That(budget.TryTake(100d), Is.True, "request " + i);
            Assert.That(budget.TryTake(100d), Is.False);
        }

        [Test]
        public void AbuseIsCountedInAWindow()
        {
            var budget = new NetBudget(10, 5, abuseLimit: 3, abuseWindowSeconds: 10);
            Assert.That(budget.NoteRefusal(0d), Is.False);
            Assert.That(budget.NoteRefusal(1d), Is.False);
            Assert.That(budget.NoteRefusal(2d), Is.False);
            Assert.That(budget.NoteRefusal(3d), Is.True, "the fourth inside the window passes the limit of 3");
        }

        [Test]
        public void OldRefusalsLeaveTheWindow()
        {
            var budget = new NetBudget(10, 5, abuseLimit: 2, abuseWindowSeconds: 10);
            budget.NoteRefusal(0d);
            budget.NoteRefusal(1d);
            Assert.That(budget.NoteRefusal(20d), Is.False, "the first two are older than the window");
            Assert.That(budget.NoteRefusal(21d), Is.False);
            Assert.That(budget.NoteRefusal(22d), Is.True);
        }
    }
}
