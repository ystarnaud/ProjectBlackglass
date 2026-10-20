using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public sealed class LootPoolHashTests
    {
        static readonly PoolLine[] Pool = { new PoolLine(1, 0, "medkit", 2), new PoolLine(0, 1, "ammo-box", 1), new PoolLine(0, 0, "stim", 3) };

        /// <summary>The value Backend ReadyReportTests pins for the same pool.</summary>
        [Test]
        public void Equals_the_backends_pinned_hash() =>
            Assert.That(LootPoolHash.Compute(1, Pool), Is.EqualTo("sha256:bce2a2eda82db4789a87efbde6bcae0fe68342ced7877883a5024dded26591c0"));

        [Test]
        public void Ignores_the_list_order() => Assert.That(LootPoolHash.Compute(1, Pool.Reverse().ToList()), Is.EqualTo(LootPoolHash.Compute(1, Pool)));

        [Test]
        public void Depends_on_the_table_version() => Assert.That(LootPoolHash.Compute(2, Pool), Is.Not.EqualTo(LootPoolHash.Compute(1, Pool)));
    }
}
