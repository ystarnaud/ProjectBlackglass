using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>Stage 12 Task 4: the ready report from the registered pool, in the exact shape ReadyReport.Parse reads.</summary>
    public sealed class ReadyReportBuilderTests
    {
        static readonly ReadyObjective[] Objectives = { new ReadyObjective("eliminate", true), new ReadyObjective("extract", false) };

        static readonly PoolLine[] Pool =
        {
            new PoolLine(1, 0, "item.light-vest", 1), new PoolLine(0, 1, "item.medkit", 2), new PoolLine(0, 0, "item.medkit", 1),
        };

        [Test]
        public void The_report_holds_the_pool_sorted_with_its_hash_and_the_layout_as_sixteen_hex_digits()
        {
            var report = ReadyReportBuilder.Build(AttemptTestData.SessionId, 0x00ab_cdef_0123_4567UL, 1, Objectives, Pool);
            Assert.That(report.processSessionId, Is.EqualTo(AttemptTestData.SessionId));
            Assert.That(report.layoutHash, Is.EqualTo("00abcdef01234567"));
            Assert.That(report.lootTableVersion, Is.EqualTo(1));
            Assert.That(report.lootPool.Select(l => (l.containerIndex, l.line, l.definitionId, l.quantity)),
                Is.EqualTo(new[] { (0, 0, "item.medkit", 1), (0, 1, "item.medkit", 2), (1, 0, "item.light-vest", 1) }));
            Assert.That(report.lootHash, Is.EqualTo(LootPoolHash.Compute(1, Pool)));
            Assert.That(report.objectives.Select(o => (o.objectiveId, o.required)), Is.EqualTo(new[] { ("eliminate", true), ("extract", false) }));
        }

        [Test]
        public void JsonUtility_writes_exactly_the_members_the_backend_parses()
        {
            var report = ReadyReportBuilder.Build(AttemptTestData.SessionId, 1UL, 1, Objectives, Pool.Take(1).ToList());
            Assert.That(JsonUtility.ToJson(report), Is.EqualTo(
                "{\"processSessionId\":\"0192f1a5-0000-7000-8000-000000000001\",\"layoutHash\":\"0000000000000001\",\"lootTableVersion\":1," +
                "\"lootHash\":\"" + LootPoolHash.Compute(1, Pool.Take(1).ToList()) + "\"," +
                "\"objectives\":[{\"objectiveId\":\"eliminate\",\"required\":true},{\"objectiveId\":\"extract\",\"required\":false}]," +
                "\"lootPool\":[{\"containerIndex\":1,\"line\":0,\"definitionId\":\"item.light-vest\",\"quantity\":1}]}"));
        }

        [Test]
        public void An_empty_pool_is_written_as_an_empty_array()
        {
            var report = ReadyReportBuilder.Build(AttemptTestData.SessionId, 1UL, 1, Objectives, new PoolLine[0]);
            Assert.That(report.lootPool, Is.Empty);
            StringAssert.Contains("\"lootPool\":[]", JsonUtility.ToJson(report));
        }
    }
}
