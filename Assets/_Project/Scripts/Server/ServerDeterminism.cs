using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The controls that remove chance from a server run (decision 045): hostiles stop acting on their own, the friendlies'
    /// own autonomy stops (except for a unit that is meant to keep it), and a unit's shots always land. They change spawned
    /// instances only, never a prefab or an asset. Shared by the bounded diagnostic and the networked server's -netQuiet.
    /// </summary>
    internal static class ServerDeterminism
    {
        /// <summary>
        /// Disables EnemyAI and AutoRetaliate on every hostile, and CompanionAI and AutoRetaliate on every friendly except
        /// `keepAutonomy` (null keeps none).
        /// </summary>
        internal static void Freeze(MissionDirector director, CommandableUnit keepAutonomy)
        {
            foreach (var hostile in director.Hostiles)
            {
                Disable<EnemyAI>(hostile);
                Disable<AutoRetaliate>(hostile);
            }
            foreach (var friendly in director.Friendlies)
            {
                if (keepAutonomy != null && friendly == keepAutonomy)
                    continue;
                Disable<CompanionAI>(friendly);
                Disable<AutoRetaliate>(friendly);
            }
        }

        /// <summary>Makes this unit's shots land: the hit roll is replaced on this instance only.</summary>
        internal static void ForceHits(CommandableUnit unit) => unit.GetComponent<UnitAttacker>().HitRoll = () => 0f;

        static void Disable<T>(Component unit) where T : Behaviour
        {
            if (unit != null && unit.TryGetComponent<T>(out var component))
                component.enabled = false;
        }
    }
}
