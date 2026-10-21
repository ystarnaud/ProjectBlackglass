using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Blackglass.Backend.Core
{
    /// <summary>One objective the generated mission holds, as the ready report registers it.</summary>
    public sealed class ReadyObjective
    {
        public ReadyObjective(string objectiveId, bool required)
        {
            ObjectiveId = objectiveId ?? throw new ArgumentNullException(nameof(objectiveId));
            Required = required;
        }

        public string ObjectiveId { get; }
        public bool Required { get; }
    }

    /// <summary>
    /// The ready report (POST /server/v1/session/ready, the backend's ReadyReport.Parse) from what the generated mission
    /// registered: the layout's 64-bit hash as 16 lowercase hex digits, the loot table version, the objectives in mission
    /// order, and the registered pool sorted by (container, line) with its SHA-256 loot hash (LootPoolHash, the backend's
    /// LootHash.Compute). It judges nothing: the backend's ObjectiveList and LootPoolValidator do.
    /// </summary>
    public static class ReadyReportBuilder
    {
        public static ReadyRequest Build(string processSessionId, ulong layoutHash, int lootTableVersion, IReadOnlyList<ReadyObjective> objectives,
            IReadOnlyList<PoolLine> pool)
        {
            if (processSessionId == null) throw new ArgumentNullException(nameof(processSessionId));
            if (objectives == null) throw new ArgumentNullException(nameof(objectives));
            if (pool == null) throw new ArgumentNullException(nameof(pool));
            var sorted = pool.OrderBy(l => l.ContainerIndex).ThenBy(l => l.Line).ToList();
            return new ReadyRequest
            {
                processSessionId = processSessionId,
                layoutHash = LayoutHashText(layoutHash),
                lootTableVersion = lootTableVersion,
                lootHash = LootPoolHash.Compute(lootTableVersion, sorted),
                objectives = objectives.Select(o => new ReadyObjectiveDto { objectiveId = o.ObjectiveId, required = o.Required }).ToArray(),
                lootPool = sorted.Select(l => new PoolLineDto { containerIndex = l.ContainerIndex, line = l.Line, definitionId = l.DefinitionId, quantity = l.Quantity }).ToArray(),
            };
        }

        /// <summary>The manifest's and the ready report's layoutHash: 16 lowercase hex digits.</summary>
        public static string LayoutHashText(ulong layoutHash) => layoutHash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
