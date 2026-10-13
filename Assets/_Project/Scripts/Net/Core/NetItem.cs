namespace Blackglass
{
    /// <summary>One stack in a container: what it is, how many, and its instance id. Sent only to a connection that searched.</summary>
    public readonly struct NetItem
    {
        public NetItem(string definitionId, int quantity, string instanceId)
        {
            DefinitionId = definitionId;
            Quantity = quantity;
            InstanceId = instanceId;
        }

        public string DefinitionId { get; }
        public int Quantity { get; }
        public string InstanceId { get; }
    }
}
