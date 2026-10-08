namespace Blackglass
{
    /// <summary>
    /// What a consumer asks about the player's knowledge when it holds an optional IntelligenceService. A null service (a
    /// scene without the feature) answers "known" to everything, so a consumer needs one call and no branch of its own.
    /// </summary>
    public static class Knowledge
    {
        /// <summary>The unit may be targeted: true for friendlies and for hostiles in live sight.</summary>
        public static bool CanTarget(IntelligenceService intelligence, Health unit) =>
            intelligence == null || intelligence.CanTarget(unit);

        /// <summary>The unit may be drawn, labelled or counted.</summary>
        public static bool IsShown(IntelligenceService intelligence, Health unit) =>
            intelligence == null || intelligence.IsUnitShown(unit);

        public static bool CanInteract(IntelligenceService intelligence, MissionInteractable item) =>
            intelligence == null || intelligence.CanInteract(item);

        public static bool CanSeeCover(IntelligenceService intelligence, CoverLocation cover) =>
            intelligence == null || intelligence.CanSeeCover(cover);

        /// <summary>The cover marker may be drawn (known ground, or the developer truth view).</summary>
        public static bool IsCoverShown(IntelligenceService intelligence, CoverLocation cover) =>
            intelligence == null || intelligence.IsCoverShown(cover);
    }
}
