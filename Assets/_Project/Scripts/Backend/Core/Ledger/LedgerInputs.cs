using System;

namespace Blackglass.Backend.Core
{
    /// <summary>One line of the attempt's registered loot pool (the ready report's lootPool entry).</summary>
    public sealed class PoolLine
    {
        public PoolLine(int containerIndex, int line, string definitionId, int quantity)
        {
            ContainerIndex = containerIndex;
            Line = line;
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Quantity = quantity;
        }

        public int ContainerIndex { get; }
        public int Line { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
    }
}
