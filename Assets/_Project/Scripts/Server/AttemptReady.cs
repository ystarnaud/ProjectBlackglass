using System.Collections.Generic;
using Blackglass.Backend.Core;

namespace Blackglass
{
    /// <summary>
    /// The ready report of a generated attempt (Stage 12 Task 4): the layout hash and loot table version of the generation, the
    /// running mission's objectives in order (the goals, then the extraction) with their required flags, and the registered
    /// pool. Nothing is sent: Stage 13 posts it.
    /// </summary>
    public static class AttemptReady
    {
        /// <summary>The pool the attempt's containers register: container c's entry i is line (c, i). Empty unless Ready.</summary>
        public static IReadOnlyList<PoolLine> Pool(MissionDirector director)
        {
            var pool = new List<PoolLine>();
            if (director == null || director.Attempt == null || director.State != MissionState.Ready)
                return pool;
            foreach (var (index, entries) in director.AttemptContainers)
                for (var line = 0; line < entries.Count; line++)
                    pool.Add(new PoolLine(index, line, entries[line].DefinitionId, entries[line].Quantity));
            return pool;
        }

        public static IReadOnlyList<ReadyObjective> Objectives(MissionRuntime runtime)
        {
            var objectives = new List<ReadyObjective>();
            if (runtime != null)
                foreach (var objective in runtime.Objectives)
                    objectives.Add(new ReadyObjective(objective.Id, objective.IsRequired));
            return objectives;
        }

        /// <summary>The report for this attempt's ready mission, or null while there is none (not Ready, another attempt, a failed generation).</summary>
        public static ReadyRequest Build(AttemptStart attempt, MissionDirector director, IReadOnlyList<PoolLine> pool)
        {
            if (attempt == null || director == null || director.State != MissionState.Ready || !ReferenceEquals(director.Attempt, attempt))
                return null;
            return ReadyReportBuilder.Build(attempt.ProcessSessionId, director.Report.LayoutHash, director.AttemptLootTableVersion,
                Objectives(director.Runtime), pool);
        }
    }
}
