using System;

namespace Blackglass
{
    /// <summary>
    /// The real visibility source in normal -net mode (hidden information slice, spec section 3.2): the squad's intelligence
    /// model (decision 037: sight, cameras, scans, exposure by firing, briefing). A connection gets squad knowledge only if it owns
    /// every living friendly that feeds the model, and at least one; anyone else observes nothing. It fails closed: with no
    /// service, a disabled one, no model or an open model (fog off) nothing is observed, and the reason is logged once.
    /// MissionIntelligence.IsObserved is deliberately not used: it answers true for untracked units and for an open model.
    /// </summary>
    public sealed class NetSquadIntel : INetIntelSource
    {
        readonly IntelligenceService service;
        readonly NetUnits units;
        readonly int friendlyCount;
        readonly Action<string> log;
        bool reported;

        public NetSquadIntel(IntelligenceService service, NetUnits units, int friendlyCount, Action<string> log)
        {
            this.service = service;
            this.units = units ?? throw new ArgumentNullException(nameof(units));
            this.friendlyCount = friendlyCount;
            this.log = log;
        }

        /// <summary>Null when the service can feed visibility; otherwise why not.</summary>
        public static string Problem(IntelligenceService service)
        {
            if (service == null)
                return "the scene has no IntelligenceService";
            if (!service.isActiveAndEnabled)
                return "the IntelligenceService is disabled";
            if (service.Model == null)
                return "the IntelligenceService has no mission model";
            if (service.Model.IsOpen)
                return "fog is off, so the intelligence model is open";
            return null;
        }

        /// <summary>True when the connection owns every living friendly of 1..friendlyCount and at least one of them.</summary>
        public static bool OwnsSquad(NetConnection connection, NetUnits units, int friendlyCount)
        {
            if (connection == null || units == null || friendlyCount <= 0)
                return false;
            var ownsAny = false;
            for (var id = 1; id <= friendlyCount; id++)
            {
                var owns = connection.Owns(id);
                ownsAny |= owns;
                if (!owns && units.TryGet(id, out var unit) && unit.IsAlive)
                    return false;
            }
            return ownsAny;
        }

        public bool Observes(NetConnection connection, int hostileId)
        {
            var problem = Problem(service);
            if (problem != null)
            {
                if (!reported)
                {
                    reported = true;
                    log?.Invoke("visibility fails closed: " + problem + "; every hostile is hidden");
                }
                return false;
            }
            reported = false;
            if (!OwnsSquad(connection, units, friendlyCount))
                return false;
            if (!units.TryGet(hostileId, out var unit) || !unit.TryGetComponent<Health>(out var health))
                return false;
            return service.Model.StateOfEnemy(health) == KnowledgeState.Observed;
        }
    }
}
