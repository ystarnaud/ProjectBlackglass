using NUnit.Framework;

namespace Blackglass.Tests
{
    public class InventoryDragRulesTests
    {
        [TestCase(InventoryListKind.Stash, InventoryListKind.Bag, InventoryCommand.ToBag)]
        [TestCase(InventoryListKind.Stash, InventoryListKind.Slot, InventoryCommand.Equip)]
        [TestCase(InventoryListKind.Bag, InventoryListKind.Slot, InventoryCommand.Equip)]
        [TestCase(InventoryListKind.Bag, InventoryListKind.Stash, InventoryCommand.ToStash)]
        [TestCase(InventoryListKind.Slot, InventoryListKind.Bag, InventoryCommand.Unequip)]
        [TestCase(InventoryListKind.Container, InventoryListKind.Bag, InventoryCommand.Take)]
        public void ADrag_MeansTheSameCommandAsTheButton(InventoryListKind from, InventoryListKind to, InventoryCommand expected)
        {
            Assert.That(InventoryDragRules.TryGetCommand(from, to, out var command), Is.True);
            Assert.That(command, Is.EqualTo(expected));
        }

        [TestCase(InventoryListKind.Slot, InventoryListKind.Stash)]
        [TestCase(InventoryListKind.Container, InventoryListKind.Slot)]
        [TestCase(InventoryListKind.Container, InventoryListKind.Stash)]
        [TestCase(InventoryListKind.Bag, InventoryListKind.Container)]
        [TestCase(InventoryListKind.Stash, InventoryListKind.Container)]
        [TestCase(InventoryListKind.Bag, InventoryListKind.Bag)]
        [TestCase(InventoryListKind.None, InventoryListKind.Bag)]
        public void ADragNobodyMeans_IsRefused(InventoryListKind from, InventoryListKind to)
        {
            Assert.That(InventoryDragRules.TryGetCommand(from, to, out _), Is.False);
        }
    }
}
