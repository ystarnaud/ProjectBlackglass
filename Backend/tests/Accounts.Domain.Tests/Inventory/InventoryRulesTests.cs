using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;

namespace Blackglass.Accounts.Domain.Tests.Inventory;

public class InventoryRulesTests
{
    static readonly CatalogueItem Rifle = new("item.rifle", ItemCategory.Weapon, ItemSlot.Weapon, false, 1, 0);
    static readonly CatalogueItem Kit = new("item.kit", ItemCategory.Consumable, ItemSlot.None, true, 3, 10);

    [Fact]
    public void Nothing_takes_no_entries()
        => Assert.Equal(0, InventoryRules.BagEntries([]));

    [Fact]
    public void Each_unit_of_a_non_stackable_item_takes_an_entry()
        => Assert.Equal(3, InventoryRules.BagEntries([(Rifle, 3)]));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(7, 3)]
    public void A_stackable_takes_ceil_of_its_total_over_the_maximum_stack(int quantity, int entries)
        => Assert.Equal(entries, InventoryRules.BagEntries([(Kit, quantity)]));

    [Fact]
    public void Grants_of_one_stackable_merge_like_ItemInventory_Add()
        => Assert.Equal(2, InventoryRules.BagEntries([(Kit, 2), (Rifle, 0), (Kit, 2)]));

    [Fact]
    public void Mixed_contents_add_up()
        => Assert.Equal(4, InventoryRules.BagEntries([(Rifle, 1), (Kit, 2), (Rifle, 1), (Kit, 2)]));

    [Fact]
    public void A_huge_quantity_does_not_overflow()
        => Assert.Equal(int.MaxValue, InventoryRules.BagEntries([(Rifle, int.MaxValue), (Rifle, int.MaxValue)]));
}
