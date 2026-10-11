using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// One owned entry: a stable instance id, the id of its definition and how many units it holds. Plain serializable
    /// data with no Unity object references. A non-stackable item is an entry of quantity 1 whose instance id never
    /// changes, wherever it is moved; a stack is one entry too (its id is not kept when the stack is split or merged).
    /// </summary>
    [Serializable]
    public sealed class ItemEntry
    {
        [SerializeField] string instanceId;
        [SerializeField] string definitionId;
        [SerializeField] int quantity;

        public ItemEntry(string definitionId, int quantity) : this(Guid.NewGuid().ToString("N"), definitionId, quantity) { }

        public ItemEntry(string instanceId, string definitionId, int quantity)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new ArgumentException("An entry needs an instance id.", nameof(instanceId));
            if (string.IsNullOrWhiteSpace(definitionId))
                throw new ArgumentException("An entry needs a definition id.", nameof(definitionId));
            if (quantity < 1)
                throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "An entry holds at least one unit.");
            this.instanceId = instanceId;
            this.definitionId = definitionId;
            this.quantity = quantity;
        }

        // For the serializer only.
        ItemEntry() { }

        public string InstanceId => instanceId;
        public string DefinitionId => definitionId;
        public int Quantity => quantity;

        internal void SetQuantity(int value) => quantity = Math.Max(0, value);

        public ItemEntry Clone() => new ItemEntry(instanceId, definitionId, quantity);
    }
}
