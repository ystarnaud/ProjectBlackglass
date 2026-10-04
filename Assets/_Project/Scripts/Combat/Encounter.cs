using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    public enum EncounterOutcome
    {
        Ongoing,
        Victory,
        Defeat,
    }

    /// <summary>
    /// The two sides of the prototype encounter, as lists of Health, and its outcome. The only place that knows who
    /// is friendly and who is hostile: enemy AI reads Friendlies from here and the HUD reads Outcome. Holds state
    /// only; everything is computed on read. No faction system.
    /// </summary>
    public sealed class Encounter : MonoBehaviour
    {
        [SerializeField] List<Health> friendlies = new List<Health>();
        [SerializeField] List<Health> hostiles = new List<Health>();

        public IReadOnlyList<Health> Friendlies => friendlies;
        public IReadOnlyList<Health> Hostiles => hostiles;

        /// <summary>Friendly entries that exist and are alive. Null and destroyed entries count as dead.</summary>
        public int LivingFriendlies => CountLiving(friendlies);

        public int LivingHostiles => CountLiving(hostiles);

        public EncounterOutcome Outcome => Resolve(friendlies.Count, LivingFriendlies, hostiles.Count, LivingHostiles);

        internal void Initialize(IEnumerable<Health> friendlyUnits, IEnumerable<Health> hostileUnits)
        {
            if (friendlyUnits == null)
                throw new ArgumentNullException(nameof(friendlyUnits));
            if (hostileUnits == null)
                throw new ArgumentNullException(nameof(hostileUnits));
            friendlies.Clear();
            friendlies.AddRange(friendlyUnits);
            hostiles.Clear();
            hostiles.AddRange(hostileUnits);
        }

        /// <summary>
        /// All friendlies dead is a defeat (checked first, so everyone dead is a defeat); otherwise all hostiles dead
        /// is a victory. An empty side never resolves, so a scene without hostiles is not an instant win.
        /// </summary>
        public static EncounterOutcome Resolve(int friendlyCount, int livingFriendlies, int hostileCount, int livingHostiles)
        {
            CheckCounts(friendlyCount, livingFriendlies, nameof(livingFriendlies));
            CheckCounts(hostileCount, livingHostiles, nameof(livingHostiles));
            if (friendlyCount > 0 && livingFriendlies == 0)
                return EncounterOutcome.Defeat;
            if (hostileCount > 0 && livingHostiles == 0)
                return EncounterOutcome.Victory;
            return EncounterOutcome.Ongoing;
        }

        static void CheckCounts(int count, int living, string livingName)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "A side cannot have a negative size.");
            if (living < 0 || living > count)
                throw new ArgumentOutOfRangeException(livingName, living, "Living units must be between 0 and the side's size.");
        }

        static int CountLiving(List<Health> units)
        {
            var living = 0;
            foreach (var unit in units)
            {
                if (unit != null && unit.IsAlive)
                    living++;
            }
            return living;
        }
    }
}
