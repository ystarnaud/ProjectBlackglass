namespace Blackglass
{
    /// <summary>
    /// What the player may know about loot (decision 037's rules extended). A container's location follows the existing
    /// static-object rule through Knowledge.CanInteract (its region is not unknown). Its contents are known only once
    /// the squad searched it: the generator knowing what is inside never makes the HUD, a panel or a log show it.
    /// </summary>
    public static class LootKnowledge
    {
        public static bool CanSeeContents(LootContainer container) => container != null && container.IsSearched;

        public static bool CanSeeLocation(IntelligenceService intelligence, LootContainer container) =>
            container != null && Knowledge.CanInteract(intelligence, container.Interactable);
    }
}
