namespace Blackglass
{
    /// <summary>The outcome of a panel request: whether it was accepted, and the line to show.</summary>
    public readonly struct RequestResult
    {
        public RequestResult(bool ok, string message)
        {
            Ok = ok;
            Message = message ?? string.Empty;
        }

        public bool Ok { get; }
        public string Message { get; }
    }

    /// <summary>
    /// What the inventory panel's buttons ask of gameplay. Each request names the operative it affects, and goes through the
    /// same APIs the rest of the game uses (SquadInventory for loadout edits, CommandableUnit.Issue for item orders, the
    /// director for deployment), so the panel holds no rules and no state. A unit that died or vanished since the last view
    /// makes the request a safe refusal with a message.
    /// </summary>
    public sealed class InventoryRequests
    {
        readonly SquadInventory inventory;
        readonly MissionDirector director;

        public InventoryRequests(SquadInventory inventory, MissionDirector director)
        {
            this.inventory = inventory;
            this.director = director;
        }

        public RequestResult Equip(string operativeId, string instanceId)
        {
            if (inventory.Core.Mode == InventoryMode.InMission)
                return SwapIn(operativeId, instanceId);
            var result = inventory.Core.Equip(operativeId, instanceId);
            return new RequestResult(result.Ok, result.Ok ? "Equipped." : result.Reason);
        }

        // In a mission an equip is a timed order for the operative (it replaces what they were doing), not an instant edit.
        RequestResult SwapIn(string operativeId, string instanceId)
        {
            var unit = Unit(operativeId, out var failure);
            if (unit == null)
                return new RequestResult(false, failure);
            if (unit.Issue(new EquipItemCommand(instanceId), IssueMode.Replace))
                return new RequestResult(true, $"Swapping gear ({UnitItems.SwapSeconds:0.#} s).");
            var items = unit.GetComponent<UnitItems>();
            return new RequestResult(false, NonEmpty(items != null ? InventoryViewBuilder.UseReason(items.LastEquipFailure) : null));
        }

        /// <summary>Takes an item from the stash into the operative's bag and equips it, in one step.</summary>
        public RequestResult EquipFromStash(string operativeId, string stashInstanceId)
        {
            var result = inventory.Core.EquipFromStash(operativeId, stashInstanceId);
            return new RequestResult(result.Ok, result.Ok ? "Equipped." : result.Reason);
        }

        public RequestResult Unequip(string operativeId, ItemSlot slot)
        {
            var result = inventory.Core.Unequip(operativeId, slot);
            return new RequestResult(result.Ok, result.Ok ? "Unequipped." : result.Reason);
        }

        public TransferResult ToStash(string operativeId, string instanceId) =>
            inventory.Core.ToStash(operativeId, instanceId, int.MaxValue);

        public TransferResult FromStash(string operativeId, string instanceId) =>
            inventory.Core.FromStash(operativeId, instanceId, int.MaxValue);

        public RequestResult Use(string operativeId, string instanceId, bool queue)
        {
            var unit = Unit(operativeId, out var failure);
            if (unit == null)
                return new RequestResult(false, failure);
            var accepted = unit.Issue(new UseItemCommand(instanceId), queue ? IssueMode.Append : IssueMode.Replace);
            if (accepted)
                return new RequestResult(true, queue ? "Use queued." : "Using.");
            var items = unit.GetComponent<UnitItems>();
            return new RequestResult(false, NonEmpty(items != null ? InventoryViewBuilder.UseReason(items.LastUseFailure) : null));
        }

        public RequestResult Take(string operativeId, LootContainer container, string instanceId, bool queue)
        {
            var unit = Unit(operativeId, out var failure);
            if (unit == null)
                return new RequestResult(false, failure);
            if (container == null)
                return new RequestResult(false, "The container is gone.");
            var accepted = unit.Issue(new CollectCommand(container, instanceId), queue ? IssueMode.Append : IssueMode.Replace);
            if (accepted)
                return new RequestResult(true, queue ? "Take queued." : "Taking.");
            var items = unit.GetComponent<UnitItems>();
            return new RequestResult(false, NonEmpty(items != null ? items.LastCollect.Describe() : null));
        }

        public RequestResult TakeAll(string operativeId, LootContainer container, bool queue) => Take(operativeId, container, null, queue);

        public RequestResult DeployNew() => director == null ? NoDirector : Deploy(director.GenerateNew());

        public RequestResult DeploySame() => director == null ? NoDirector : Deploy(director.RegenerateSame());

        static RequestResult NoDirector => new RequestResult(false, "No mission director in this scene.");

        static RequestResult Deploy(bool started) =>
            started ? new RequestResult(true, "Deploying.") : new RequestResult(false, "A mission is being generated. Try again in a moment.");

        // A refusal always carries a line the panel can show, even when the unit recorded no reason.
        static string NonEmpty(string reason) => string.IsNullOrEmpty(reason) ? "That cannot be done now." : reason;

        CommandableUnit Unit(string operativeId, out string failure)
        {
            var unit = InventoryViewBuilder.FindUnit(director, operativeId);
            failure = unit == null ? "That operative is not on the mission." : !unit.IsAlive ? "That operative is down." : string.Empty;
            return failure.Length == 0 ? unit : null;
        }
    }
}
