namespace Blackglass
{
    /// <summary>
    /// What the player knows about one thing. Regions and devices: Discovered is known and permanent, Observed is in sight
    /// right now. Enemies: Discovered means last known (a marker at the last seen position), Observed is live. One enum for
    /// all three, so every consumer asks the same question.
    /// </summary>
    public enum KnowledgeState
    {
        Unknown,
        Discovered,
        Observed,
    }
}
