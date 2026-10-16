using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.Domain.Tests.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Inventory;

public class InventoryEditorTests
{
    const long Op1 = 1;
    const long Op2 = 2;
    const string Rifle = "item.rifle";
    const string Vest = "item.vest";
    const string Boots = "item.boots";
    const string Junk = "item.junk";     // non-stackable, no slot
    const string Kit = "item.kit";       // stackable, max 3
    const string Ammo = "item.ammo";     // stackable, max 5
    const string Charm = "item.charm";   // stackable with a slot
    const string Retired = "item.retired"; // not in the catalogue

    static CatalogueDocument Cat(int capacity = 4) => Golden.Document() with
    {
        Inventory = new(capacity),
        Items =
        [
            new(Rifle, ItemCategory.Weapon, ItemSlot.Weapon, false, 1, 0),
            new(Vest, ItemCategory.Armor, ItemSlot.Armor, false, 1, 0),
            new(Boots, ItemCategory.Utility, ItemSlot.Utility, false, 1, 0),
            new(Junk, ItemCategory.Utility, ItemSlot.None, false, 1, 0),
            new(Kit, ItemCategory.Consumable, ItemSlot.None, true, 3, 10),
            new(Ammo, ItemCategory.Consumable, ItemSlot.None, true, 5, 0),
            new(Charm, ItemCategory.Utility, ItemSlot.Utility, true, 2, 0),
        ],
    };

    static HeldInstance Inst(long id, string definition, long? holder, ItemSlot? equipped = null) => new(id, definition, holder, equipped);
    static HeldStack Stack(long? holder, string definition, int quantity) => new(holder, definition, quantity);

    static InventoryModel Model(IEnumerable<HeldInstance>? instances = null, IEnumerable<HeldStack>? stacks = null) =>
        new([Op1, Op2], (instances ?? []).ToList(), (stacks ?? []).ToList());

    static EditOutcome Run(InventoryModel model, int capacity, params InventoryOperation[] operations) =>
        InventoryEditor.Apply(model, Cat(capacity), operations);

    static InventoryModel Applied(EditOutcome outcome)
    {
        Assert.True(outcome.Applied, $"refused at {outcome.RefusedIndex}: {outcome.Refusal}");
        Assert.Null(outcome.RefusedIndex);
        Assert.Null(outcome.Refusal);
        return outcome.After!;
    }

    static void AssertRefused(EditOutcome outcome, int index, InventoryRefusal refusal)
    {
        Assert.False(outcome.Applied);
        Assert.Null(outcome.After);
        Assert.Equal(index, outcome.RefusedIndex);
        Assert.Equal(refusal, outcome.Refusal);
    }

    static HeldInstance Find(InventoryModel model, long itemId) => model.Instances.Single(i => i.ItemId == itemId);

    static int Quantity(InventoryModel model, long? holder, string definition) =>
        model.Stacks.Where(s => s.OperativeId == holder && s.DefinitionId == definition).Sum(s => s.Quantity);

    static int Total(InventoryModel model, string definition) =>
        model.Stacks.Where(s => s.DefinitionId == definition).Sum(s => s.Quantity);

    static string Snapshot(InventoryModel model) =>
        string.Join("|", model.Operatives) + "#" + string.Join("|", model.Instances) + "#" + string.Join("|", model.Stacks);

    // ---------------------------------------------------------------- Equip

    [Fact]
    public void Equip_for_an_unknown_operative_is_UnknownOperative()
        => AssertRefused(Run(Model([Inst(100, Rifle, Op1)]), 4, new Equip(99, 100)), 0, InventoryRefusal.UnknownOperative);

    [Fact]
    public void Equip_of_an_item_not_in_that_bag_is_NotFound()
    {
        var model = Model([Inst(100, Rifle, Op2), Inst(101, Vest, null)]);

        AssertRefused(Run(model, 4, new Equip(Op1, 100)), 0, InventoryRefusal.NotFound);   // another operative's bag
        AssertRefused(Run(model, 4, new Equip(Op1, 101)), 0, InventoryRefusal.NotFound);   // the stash
        AssertRefused(Run(model, 4, new Equip(Op1, 555)), 0, InventoryRefusal.NotFound);   // nowhere
    }

    [Fact]
    public void Equip_of_a_retired_definition_is_UnknownItem()
        => AssertRefused(Run(Model([Inst(100, Retired, Op1)]), 4, new Equip(Op1, 100)), 0, InventoryRefusal.UnknownItem);

    [Fact]
    public void Equip_of_an_item_without_a_slot_is_NotEquippable()
        => AssertRefused(Run(Model([Inst(100, Junk, Op1)]), 4, new Equip(Op1, 100)), 0, InventoryRefusal.NotEquippable);

    [Fact]
    public void Equip_takes_the_slot()
    {
        var after = Applied(Run(Model([Inst(100, Rifle, Op1)]), 4, new Equip(Op1, 100)));

        Assert.Equal(new HeldInstance(100, Rifle, Op1, ItemSlot.Weapon), Find(after, 100));
    }

    [Fact]
    public void Re_equipping_the_equipped_item_succeeds()
    {
        var after = Applied(Run(Model([Inst(100, Rifle, Op1, ItemSlot.Weapon)]), 4, new Equip(Op1, 100)));

        Assert.Equal(ItemSlot.Weapon, Find(after, 100).Equipped);
    }

    [Fact]
    public void Equip_displaces_the_occupant_which_stays_in_the_bag()
    {
        var model = Model([Inst(100, Rifle, Op1, ItemSlot.Weapon), Inst(101, Rifle, Op1)]);

        var after = Applied(Run(model, 4, new Equip(Op1, 101)));

        Assert.Equal(new HeldInstance(100, Rifle, Op1, null), Find(after, 100));
        Assert.Equal(new HeldInstance(101, Rifle, Op1, ItemSlot.Weapon), Find(after, 101));
    }

    // -------------------------------------------------------------- Unequip

    [Fact]
    public void Unequip_for_an_unknown_operative_is_UnknownOperative()
        => AssertRefused(Run(Model([Inst(100, Rifle, Op1, ItemSlot.Weapon)]), 4, new Unequip(99, ItemSlot.Weapon)), 0, InventoryRefusal.UnknownOperative);

    [Fact]
    public void Unequip_of_an_empty_slot_is_NothingEquipped()
    {
        var model = Model([Inst(100, Rifle, Op1, ItemSlot.Weapon), Inst(101, Vest, Op2, ItemSlot.Armor)]);

        AssertRefused(Run(model, 4, new Unequip(Op1, ItemSlot.Armor)), 0, InventoryRefusal.NothingEquipped);   // another operative's slot
        AssertRefused(Run(model, 4, new Unequip(Op1, ItemSlot.None)), 0, InventoryRefusal.NothingEquipped);
    }

    [Fact]
    public void Unequip_leaves_the_item_in_the_bag()
    {
        var after = Applied(Run(Model([Inst(100, Rifle, Op1, ItemSlot.Weapon)]), 4, new Unequip(Op1, ItemSlot.Weapon)));

        Assert.Equal(new HeldInstance(100, Rifle, Op1, null), Find(after, 100));
    }

    // ----------------------------------------------- MoveInstance to the stash

    [Fact]
    public void ToStash_for_an_unknown_operative_is_UnknownOperative()
        => AssertRefused(Run(Model([Inst(100, Rifle, Op1)]), 4, new MoveInstance(99, 100, Destination.Stash)), 0, InventoryRefusal.UnknownOperative);

    [Fact]
    public void ToStash_of_an_equipped_item_is_Equipped()
        => AssertRefused(Run(Model([Inst(100, Rifle, Op1, ItemSlot.Weapon)]), 4, new MoveInstance(Op1, 100, Destination.Stash)), 0, InventoryRefusal.Equipped);

    [Fact]
    public void ToStash_of_an_item_not_in_the_bag_is_NotFound()
    {
        var model = Model([Inst(100, Rifle, Op2), Inst(101, Vest, null)]);

        AssertRefused(Run(model, 4, new MoveInstance(Op1, 100, Destination.Stash)), 0, InventoryRefusal.NotFound);
        AssertRefused(Run(model, 4, new MoveInstance(Op1, 101, Destination.Stash)), 0, InventoryRefusal.NotFound);
        AssertRefused(Run(model, 4, new MoveInstance(Op1, 555, Destination.Stash)), 0, InventoryRefusal.NotFound);
    }

    [Fact]
    public void ToStash_moves_the_item_and_the_stash_has_no_cap()
    {
        var stash = Enumerable.Range(0, 50).Select(n => Inst(200 + n, Boots, null));
        var model = Model(stash.Append(Inst(100, Rifle, Op1)));

        var after = Applied(Run(model, 4, new MoveInstance(Op1, 100, Destination.Stash)));

        Assert.Equal(new HeldInstance(100, Rifle, null, null), Find(after, 100));
        Assert.Equal(51, after.Instances.Count(i => i.OperativeId is null));
    }

    // -------------------------------------------------- MoveInstance to a bag

    [Fact]
    public void ToBag_for_an_unknown_operative_is_UnknownOperative()
        => AssertRefused(Run(Model([Inst(100, Rifle, null)]), 4, new MoveInstance(99, 100, Destination.Bag)), 0, InventoryRefusal.UnknownOperative);

    [Fact]
    public void ToBag_of_an_item_not_in_the_stash_is_NotFound()
    {
        var model = Model([Inst(100, Rifle, Op2), Inst(101, Vest, Op1)]);

        AssertRefused(Run(model, 4, new MoveInstance(Op1, 100, Destination.Bag)), 0, InventoryRefusal.NotFound);
        AssertRefused(Run(model, 4, new MoveInstance(Op1, 101, Destination.Bag)), 0, InventoryRefusal.NotFound);
        AssertRefused(Run(model, 4, new MoveInstance(Op1, 555, Destination.Bag)), 0, InventoryRefusal.NotFound);
    }

    [Fact]
    public void ToBag_of_a_retired_definition_is_UnknownItem()
        => AssertRefused(Run(Model([Inst(100, Retired, null)]), 4, new MoveInstance(Op1, 100, Destination.Bag)), 0, InventoryRefusal.UnknownItem);

    [Fact]
    public void ToBag_without_a_free_entry_is_DestinationFull()
    {
        var model = Model([Inst(100, Rifle, null), Inst(1, Vest, Op1), Inst(2, Vest, Op1)]);

        AssertRefused(Run(model, 2, new MoveInstance(Op1, 100, Destination.Bag)), 0, InventoryRefusal.DestinationFull);
    }

    [Fact]
    public void ToBag_moves_the_item_into_the_bag_unequipped()
    {
        var after = Applied(Run(Model([Inst(100, Rifle, null)]), 4, new MoveInstance(Op1, 100, Destination.Bag)));

        Assert.Equal(new HeldInstance(100, Rifle, Op1, null), Find(after, 100));
    }

    // ------------------------------------------------------------ MoveStack

    [Theory]
    [InlineData(Destination.Bag)]
    [InlineData(Destination.Stash)]
    public void A_stack_move_for_an_unknown_operative_is_UnknownOperative(Destination to)
    {
        var model = Model(stacks: [Stack(null, Kit, 3), Stack(Op1, Kit, 3)]);

        AssertRefused(Run(model, 4, new MoveStack(99, Kit, 1, to)), 0, InventoryRefusal.UnknownOperative);
    }

    [Theory]
    [InlineData(Destination.Bag, 0)]
    [InlineData(Destination.Bag, -1)]
    [InlineData(Destination.Stash, 0)]
    [InlineData(Destination.Stash, -5)]
    public void A_stack_move_below_one_is_InvalidQuantity(Destination to, int quantity)
    {
        var model = Model(stacks: [Stack(null, Kit, 3), Stack(Op1, Kit, 3)]);

        AssertRefused(Run(model, 4, new MoveStack(Op1, Kit, quantity, to)), 0, InventoryRefusal.InvalidQuantity);
    }

    [Fact]
    public void Quantity_is_checked_before_the_source_lookup()
        => AssertRefused(Run(Model(), 4, new MoveStack(Op1, Kit, 0, Destination.Bag)), 0, InventoryRefusal.InvalidQuantity);

    [Fact]
    public void A_stack_move_without_a_source_row_is_NotFound()
    {
        var model = Model(stacks: [Stack(Op2, Kit, 3), Stack(null, Ammo, 2)]);

        AssertRefused(Run(model, 4, new MoveStack(Op1, Kit, 1, Destination.Bag)), 0, InventoryRefusal.NotFound);     // stash has no kit; op2's does not count
        AssertRefused(Run(model, 4, new MoveStack(Op1, Ammo, 1, Destination.Stash)), 0, InventoryRefusal.NotFound);  // the bag has no ammo
    }

    [Fact]
    public void A_retired_stack_to_a_bag_is_UnknownItem()
        => AssertRefused(Run(Model(stacks: [Stack(null, Retired, 3)]), 4, new MoveStack(Op1, Retired, 1, Destination.Bag)), 0, InventoryRefusal.UnknownItem);

    [Fact]
    public void More_than_held_is_InsufficientQuantity_not_a_clamp()
    {
        var model = Model(stacks: [Stack(null, Kit, 3), Stack(Op1, Kit, 2)]);

        AssertRefused(Run(model, 8, new MoveStack(Op1, Kit, 4, Destination.Bag)), 0, InventoryRefusal.InsufficientQuantity);
        AssertRefused(Run(model, 8, new MoveStack(Op1, Kit, 3, Destination.Stash)), 0, InventoryRefusal.InsufficientQuantity);
    }

    [Fact]
    public void Partial_room_is_DestinationFull_not_a_partial_move()
    {
        // Free entries: 1. Six kits need two entries, so nothing moves although one entry's worth would fit.
        var model = Model([Inst(1, Vest, Op1), Inst(2, Vest, Op1), Inst(3, Vest, Op1)], [Stack(null, Kit, 6)]);

        var outcome = Run(model, 4, new MoveStack(Op1, Kit, 6, Destination.Bag));

        AssertRefused(outcome, 0, InventoryRefusal.DestinationFull);
        Assert.Equal(6, Quantity(model, null, Kit));
    }

    [Fact]
    public void A_stack_fills_its_open_entry_in_a_full_bag()
    {
        // 3 instances + one kit entry (2 of 3) = 4 of 4 entries. One more kit fits the open entry.
        var model = Model([Inst(1, Vest, Op1), Inst(2, Vest, Op1), Inst(3, Vest, Op1)], [Stack(Op1, Kit, 2), Stack(null, Kit, 1)]);

        var after = Applied(Run(model, 4, new MoveStack(Op1, Kit, 1, Destination.Bag)));

        Assert.Equal(3, Quantity(after, Op1, Kit));
        Assert.Equal(0, Quantity(after, null, Kit));
    }

    [Fact]
    public void A_stack_needing_one_more_entry_than_free_is_DestinationFull()
    {
        // 3 instances + one full kit entry (3 of 3) = 4 of 4. A fourth kit would start a second entry.
        var model = Model([Inst(1, Vest, Op1), Inst(2, Vest, Op1), Inst(3, Vest, Op1)], [Stack(Op1, Kit, 3), Stack(null, Kit, 1)]);

        AssertRefused(Run(model, 4, new MoveStack(Op1, Kit, 1, Destination.Bag)), 0, InventoryRefusal.DestinationFull);
    }

    [Fact]
    public void Exactly_filling_the_bag_succeeds()
    {
        var model = Model([Inst(1, Vest, Op1), Inst(2, Vest, Op1)], [Stack(null, Kit, 7)]);

        var after = Applied(Run(model, 4, new MoveStack(Op1, Kit, 6, Destination.Bag)));   // two entries into two free
        Assert.Equal(4, InventoryEditor.BagEntries(after, Cat(4), Op1));

        AssertRefused(Run(model, 4, new MoveStack(Op1, Kit, 7, Destination.Bag)), 0, InventoryRefusal.DestinationFull);   // three entries
    }

    [Fact]
    public void A_stack_move_merges_into_the_destination_row()
    {
        var model = Model(stacks: [Stack(null, Kit, 5), Stack(Op1, Kit, 1)]);

        var after = Applied(Run(model, 4, new MoveStack(Op1, Kit, 2, Destination.Bag)));

        Assert.Equal([Stack(null, Kit, 3), Stack(Op1, Kit, 3)], after.Stacks.OrderBy(s => s.OperativeId ?? 0));
    }

    [Fact]
    public void A_stack_move_to_the_stash_is_uncapped()
    {
        var model = Model(stacks: [Stack(Op1, Kit, 30), Stack(null, Kit, 1000)]);

        var after = Applied(Run(model, 4, new MoveStack(Op1, Kit, 25, Destination.Stash)));

        Assert.Equal(1025, Quantity(after, null, Kit));
        Assert.Equal(5, Quantity(after, Op1, Kit));
    }

    // ------------------------------------------------------- EquipFromStash

    [Fact]
    public void EquipFromStash_for_an_unknown_operative_is_UnknownOperative()
        => AssertRefused(Run(Model([Inst(100, Rifle, null)]), 4, new EquipFromStash(99, 100)), 0, InventoryRefusal.UnknownOperative);

    [Fact]
    public void EquipFromStash_of_an_item_not_in_the_stash_is_NotFound()
    {
        var model = Model([Inst(100, Rifle, Op1), Inst(101, Rifle, Op2)]);

        AssertRefused(Run(model, 4, new EquipFromStash(Op1, 100)), 0, InventoryRefusal.NotFound);
        AssertRefused(Run(model, 4, new EquipFromStash(Op1, 101)), 0, InventoryRefusal.NotFound);
        AssertRefused(Run(model, 4, new EquipFromStash(Op1, 555)), 0, InventoryRefusal.NotFound);
    }

    [Fact]
    public void EquipFromStash_of_a_retired_definition_is_UnknownItem()
        => AssertRefused(Run(Model([Inst(100, Retired, null)]), 4, new EquipFromStash(Op1, 100)), 0, InventoryRefusal.UnknownItem);

    [Fact]
    public void EquipFromStash_of_an_item_without_a_slot_is_NotEquippable()
    {
        AssertRefused(Run(Model([Inst(100, Junk, null)]), 4, new EquipFromStash(Op1, 100)), 0, InventoryRefusal.NotEquippable);
        // A stackable definition is not equippable even when the catalogue gives it a slot (and a stack has no instance anyway).
        AssertRefused(Run(Model([Inst(101, Charm, null)]), 4, new EquipFromStash(Op1, 101)), 0, InventoryRefusal.NotEquippable);
    }

    [Fact]
    public void EquipFromStash_without_a_free_entry_is_DestinationFull()
    {
        var model = Model([Inst(100, Rifle, null), Inst(1, Vest, Op1), Inst(2, Vest, Op1)]);

        AssertRefused(Run(model, 2, new EquipFromStash(Op1, 100)), 0, InventoryRefusal.DestinationFull);
    }

    [Fact]
    public void EquipFromStash_moves_and_equips_displacing_the_occupant()
    {
        var model = Model([Inst(100, Rifle, null), Inst(101, Rifle, Op1, ItemSlot.Weapon)]);

        var after = Applied(Run(model, 4, new EquipFromStash(Op1, 100)));

        Assert.Equal(new HeldInstance(100, Rifle, Op1, ItemSlot.Weapon), Find(after, 100));
        Assert.Equal(new HeldInstance(101, Rifle, Op1, null), Find(after, 101));
    }

    // -------------------------------------------- Displacement and batches

    [Fact]
    public void A_batch_sees_its_own_earlier_operations()
    {
        var model = Model([Inst(100, Vest, Op1)]);

        var after = Applied(Run(model, 4, new MoveInstance(Op1, 100, Destination.Stash), new EquipFromStash(Op2, 100)));

        Assert.Equal(new HeldInstance(100, Vest, Op2, ItemSlot.Armor), Find(after, 100));
    }

    [Fact]
    public void Several_operations_on_one_item_in_a_batch()
    {
        var model = Model([Inst(100, Vest, null)]);

        var after = Applied(Run(model, 4,
            new MoveInstance(Op1, 100, Destination.Bag),
            new Equip(Op1, 100),
            new Unequip(Op1, ItemSlot.Armor),
            new MoveInstance(Op1, 100, Destination.Stash)));

        Assert.Equal(new HeldInstance(100, Vest, null, null), Find(after, 100));
    }

    [Fact]
    public void The_first_refusal_stops_the_batch_and_returns_the_untouched_model()
    {
        var model = Model([Inst(100, Rifle, Op1), Inst(101, Vest, Op1)]);
        var snapshot = Snapshot(model);

        var outcome = Run(model, 4,
            new Equip(Op1, 100),
            new Unequip(Op1, ItemSlot.Armor),   // nothing in the armor slot
            new Equip(Op1, 101));

        AssertRefused(outcome, 1, InventoryRefusal.NothingEquipped);
        Assert.Equal(snapshot, Snapshot(model));
    }

    [Fact]
    public void A_refused_equipFromStash_leaves_the_item_in_the_stash_unequipped()
    {
        var model = Model([Inst(100, Junk, null), Inst(101, Rifle, Op1, ItemSlot.Weapon)]);
        var snapshot = Snapshot(model);

        var outcome = Run(model, 4, new Unequip(Op1, ItemSlot.Weapon), new EquipFromStash(Op1, 100));

        AssertRefused(outcome, 1, InventoryRefusal.NotEquippable);
        Assert.Equal(snapshot, Snapshot(model));
        Assert.Equal(new HeldInstance(100, Junk, null, null), Find(model, 100));
    }

    [Fact]
    public void A_bag_to_bag_move_is_toStash_then_toBag()
    {
        var model = Model([Inst(100, Boots, Op1)]);

        AssertRefused(Run(model, 4, new MoveInstance(Op2, 100, Destination.Bag)), 0, InventoryRefusal.NotFound);

        var after = Applied(Run(model, 4, new MoveInstance(Op1, 100, Destination.Stash), new MoveInstance(Op2, 100, Destination.Bag)));
        Assert.Equal(new HeldInstance(100, Boots, Op2, null), Find(after, 100));
    }

    [Fact]
    public void An_empty_batch_is_an_argument_error()
        => Assert.Throws<ArgumentException>(() => InventoryEditor.Apply(Model(), Cat(), []));

    // ------------------------------------------------- Conservation and stacks

    [Fact]
    public void Splitting_and_merging_conserve_quantity()
    {
        var model = Model(stacks: [Stack(null, Kit, 4), Stack(null, Ammo, 5)]);

        var split = Applied(Run(model, 8, new MoveStack(Op1, Kit, 1, Destination.Bag), new MoveStack(Op2, Kit, 2, Destination.Bag)));
        Assert.Equal(4, Total(split, Kit));
        Assert.Equal(1, Quantity(split, Op1, Kit));
        Assert.Equal(2, Quantity(split, Op2, Kit));
        Assert.Equal(1, Quantity(split, null, Kit));

        var merged = Applied(Run(split, 8,
            new MoveStack(Op1, Kit, 1, Destination.Stash),
            new MoveStack(Op2, Kit, 2, Destination.Stash)));
        Assert.Equal(4, Total(merged, Kit));
        Assert.Equal(4, Quantity(merged, null, Kit));
        Assert.Single(merged.Stacks, s => s.DefinitionId == Kit);
        Assert.Equal(5, Total(merged, Ammo));
    }

    [Fact]
    public void An_emptied_stack_disappears_from_the_model()
    {
        var model = Model(stacks: [Stack(null, Kit, 2)]);

        var toBag = Applied(Run(model, 4, new MoveStack(Op1, Kit, 2, Destination.Bag)));
        Assert.Equal([Stack(Op1, Kit, 2)], toBag.Stacks);

        var back = Applied(Run(toBag, 4, new MoveStack(Op1, Kit, 2, Destination.Stash)));
        Assert.Equal([Stack(null, Kit, 2)], back.Stacks);
    }

    [Fact]
    public void Instances_keep_their_ids_through_every_move()
    {
        var model = Model([Inst(100, Rifle, Op1, ItemSlot.Weapon), Inst(101, Vest, null), Inst(102, Boots, Op2)]);
        var snapshot = Snapshot(model);

        var after = Applied(Run(model, 6,
            new Unequip(Op1, ItemSlot.Weapon),
            new MoveInstance(Op1, 100, Destination.Stash),
            new MoveInstance(Op2, 100, Destination.Bag),
            new MoveInstance(Op2, 102, Destination.Stash),
            new EquipFromStash(Op1, 101),
            new MoveInstance(Op1, 102, Destination.Bag)));

        Assert.Equal([100L, 101, 102], after.Instances.Select(i => i.ItemId).Order());
        Assert.All(after.Instances, i => Assert.Equal(model.Instances.Single(b => b.ItemId == i.ItemId).DefinitionId, i.DefinitionId));
        Assert.Equal(snapshot, Snapshot(model));   // the caller's model is never modified
    }

    // ------------------------------------------------------------ Over capacity

    [Fact]
    public void Over_capacity_bags_allow_toStash_equip_and_unequip_but_not_toBag()
    {
        // Capacity 2, the bag holds 3 entries (a catalogue lowered it) plus a kit entry.
        InventoryModel Over() => Model(
            [Inst(1, Rifle, Op1, ItemSlot.Weapon), Inst(2, Vest, Op1), Inst(3, Boots, Op1), Inst(100, Rifle, null)],
            [Stack(Op1, Kit, 2), Stack(null, Kit, 1), Stack(null, Ammo, 1)]);

        Applied(Run(Over(), 2, new Equip(Op1, 2)));
        Applied(Run(Over(), 2, new Unequip(Op1, ItemSlot.Weapon)));
        Applied(Run(Over(), 2, new MoveInstance(Op1, 2, Destination.Stash)));
        Applied(Run(Over(), 2, new MoveStack(Op1, Kit, 2, Destination.Stash)));
        AssertRefused(Run(Over(), 2, new MoveInstance(Op1, 100, Destination.Bag)), 0, InventoryRefusal.DestinationFull);
        AssertRefused(Run(Over(), 2, new EquipFromStash(Op1, 100)), 0, InventoryRefusal.DestinationFull);
        AssertRefused(Run(Over(), 2, new MoveStack(Op1, Ammo, 1, Destination.Bag)), 0, InventoryRefusal.DestinationFull);

        // Units that fit an open stack do not grow the bag, so they may still go in.
        var after = Applied(Run(Over(), 2, new MoveStack(Op1, Kit, 1, Destination.Bag)));
        Assert.Equal(3, Quantity(after, Op1, Kit));
    }

    // -------------------------------------------------- Retired definitions

    [Fact]
    public void A_retired_item_can_be_unequipped_and_moved_to_the_stash()
    {
        var model = Model([Inst(100, Retired, Op1, ItemSlot.Armor), Inst(101, Retired, Op1)]);

        var after = Applied(Run(model, 4, new Unequip(Op1, ItemSlot.Armor), new MoveInstance(Op1, 100, Destination.Stash), new MoveInstance(Op1, 101, Destination.Stash)));

        Assert.Equal(new HeldInstance(100, Retired, null, null), Find(after, 100));
        Assert.Equal(new HeldInstance(101, Retired, null, null), Find(after, 101));
    }

    [Fact]
    public void A_retired_stack_can_be_moved_to_the_stash_whole_or_in_part()
    {
        var model = Model(stacks: [Stack(Op1, Retired, 5)]);

        var part = Applied(Run(model, 4, new MoveStack(Op1, Retired, 2, Destination.Stash)));
        Assert.Equal(3, Quantity(part, Op1, Retired));
        Assert.Equal(2, Quantity(part, null, Retired));
        Assert.Equal(1, InventoryEditor.BagEntries(part, Cat(4), Op1));   // the remainder still takes one entry

        var whole = Applied(Run(part, 4, new MoveStack(Op1, Retired, 3, Destination.Stash)));
        Assert.Equal([Stack(null, Retired, 5)], whole.Stacks);
    }

    [Fact]
    public void A_retired_item_cannot_be_equipped_moved_to_a_bag_or_equipped_from_the_stash()
    {
        var model = Model([Inst(100, Retired, Op1), Inst(101, Retired, null)], [Stack(null, Retired, 2)]);

        AssertRefused(Run(model, 4, new Equip(Op1, 100)), 0, InventoryRefusal.UnknownItem);
        AssertRefused(Run(model, 4, new MoveInstance(Op1, 101, Destination.Bag)), 0, InventoryRefusal.UnknownItem);
        AssertRefused(Run(model, 4, new EquipFromStash(Op1, 101)), 0, InventoryRefusal.UnknownItem);
        AssertRefused(Run(model, 4, new MoveStack(Op1, Retired, 1, Destination.Bag)), 0, InventoryRefusal.UnknownItem);
    }

    [Fact]
    public void BagEntries_counts_instances_canonical_stacks_and_unknown_rows()
    {
        var model = Model(
            [Inst(1, Rifle, Op1, ItemSlot.Weapon), Inst(2, Retired, Op1), Inst(3, Vest, Op2), Inst(4, Boots, null)],
            [Stack(Op1, Kit, 7), Stack(Op1, Ammo, 5), Stack(Op1, Retired, 9), Stack(Op2, Kit, 1), Stack(null, Kit, 50)]);

        // 2 instances + ceil(7/3) + ceil(5/5) + 1 unknown row
        Assert.Equal(2 + 3 + 1 + 1, InventoryEditor.BagEntries(model, Cat(), Op1));
        Assert.Equal(1 + 1, InventoryEditor.BagEntries(model, Cat(), Op2));
    }

    // ----------------------------------------------------------- Final state

    static InventoryModel FinalBefore() => Model(
        [Inst(100, Rifle, Op1, ItemSlot.Weapon), Inst(103, Rifle, Op1), Inst(101, Vest, Op1), Inst(102, Boots, null)],
        [Stack(Op1, Kit, 2), Stack(null, Kit, 4)]);

    static InventoryModel Derived(Func<List<HeldInstance>, List<HeldStack>, InventoryModel> change)
    {
        var before = FinalBefore();
        return change([.. before.Instances], [.. before.Stacks]);
    }

    static InventoryModel Same(List<HeldInstance> i, List<HeldStack> s) => new([Op1, Op2], i, s);

    public static IEnumerable<object[]> BrokenAfters()
    {
        yield return ["a created instance", Derived((i, s) => { i.Add(Inst(999, Boots, null)); return Same(i, s); })];
        yield return ["a lost instance", Derived((i, s) => { i.RemoveAll(x => x.ItemId == 102); return Same(i, s); })];
        yield return ["a changed instance id", Derived((i, s) => { i[3] = i[3] with { ItemId = 104 }; return Same(i, s); })];
        yield return ["a changed definition", Derived((i, s) => { i[3] = i[3] with { DefinitionId = Vest }; return Same(i, s); })];
        yield return ["a duplicated instance id", Derived((i, s) => { i[3] = i[3] with { ItemId = 100 }; return Same(i, s); })];
        yield return ["created stack units", Derived((i, s) => { s[1] = s[1] with { Quantity = 5 }; return Same(i, s); })];
        yield return ["lost stack units", Derived((i, s) => { s[1] = s[1] with { Quantity = 3 }; return Same(i, s); })];
        yield return ["a lost stack row", Derived((i, s) => { s.RemoveAt(0); return Same(i, s); })];
        yield return ["a zero stack", Derived((i, s) => { s[0] = s[0] with { Quantity = 0 }; s[1] = s[1] with { Quantity = 6 }; return Same(i, s); })];
        yield return ["a duplicate stack row", Derived((i, s) => { s[0] = s[0] with { Quantity = 1 }; s.Add(Stack(Op1, Kit, 1)); return Same(i, s); })];
        yield return ["a stack row of an unknown operative", Derived((i, s) => { s[0] = s[0] with { OperativeId = 99 }; return Same(i, s); })];
        yield return ["an equipped item in the stash", Derived((i, s) => { i[3] = i[3] with { Equipped = ItemSlot.Utility }; return Same(i, s); })];
        yield return ["an equipped item in an unknown operative's bag", Derived((i, s) => { i[3] = i[3] with { OperativeId = 99, Equipped = ItemSlot.Utility }; return Same(i, s); })];
        yield return ["an item held by an unknown operative", Derived((i, s) => { i[3] = i[3] with { OperativeId = 99 }; return Same(i, s); })];
        yield return ["a slot mismatch", Derived((i, s) => { i[2] = i[2] with { Equipped = ItemSlot.Weapon }; return Same(i, s); })];
        yield return ["an equipped slot of none", Derived((i, s) => { i[1] = i[1] with { Equipped = ItemSlot.None }; return Same(i, s); })];
        yield return ["two items in one slot", Derived((i, s) => { i[1] = i[1] with { Equipped = ItemSlot.Weapon }; return Same(i, s); })];
        yield return ["a bag grown past capacity", Derived((i, s) => { i[3] = i[3] with { OperativeId = Op1 }; return Same(i, s); })];
        yield return ["a changed operative set", Derived((i, s) => new InventoryModel([Op1], i, s))];
    }

    [Theory]
    [MemberData(nameof(BrokenAfters))]
    public void ValidateFinal_rejects_each_broken_invariant(string broken, InventoryModel after)
    {
        _ = broken;
        // Capacity 4: Op1's bag holds rifle, rifle, vest and one kit entry, exactly full.
        Assert.Throws<InventoryInvariantException>(() => InventoryEditor.ValidateFinal(FinalBefore(), after, Cat(4)));
    }

    [Fact]
    public void ValidateFinal_accepts_the_unchanged_model_and_a_valid_edit()
    {
        InventoryEditor.ValidateFinal(FinalBefore(), FinalBefore(), Cat(4));

        var after = Applied(Run(FinalBefore(), 4, new Equip(Op1, 103), new MoveInstance(Op1, 101, Destination.Stash)));
        InventoryEditor.ValidateFinal(FinalBefore(), after, Cat(4));
    }

    [Fact]
    public void ValidateFinal_allows_an_over_capacity_bag_that_did_not_grow()
    {
        // Capacity 3, the bag holds 4 entries before and after.
        var before = FinalBefore();
        var after = Derived((i, s) => { i[0] = i[0] with { Equipped = null }; i[1] = i[1] with { Equipped = ItemSlot.Weapon }; return Same(i, s); });

        InventoryEditor.ValidateFinal(before, after, Cat(3));
        Assert.Equal(4, InventoryEditor.BagEntries(after, Cat(3), Op1));

        // Shrinking is allowed too; growing is not.
        var smaller = Derived((i, s) => { i[2] = i[2] with { OperativeId = null }; return Same(i, s); });
        InventoryEditor.ValidateFinal(before, smaller, Cat(3));
        var bigger = Derived((i, s) => { i[3] = i[3] with { OperativeId = Op1 }; return Same(i, s); });
        Assert.Throws<InventoryInvariantException>(() => InventoryEditor.ValidateFinal(before, bigger, Cat(3)));
    }

    [Fact]
    public void ValidateFinal_does_not_judge_an_untouched_retired_equipped_item()
    {
        var before = Model([Inst(100, Retired, Op1, ItemSlot.Armor), Inst(101, Rifle, Op1)]);

        var after = Applied(Run(before, 4, new Equip(Op1, 101)));   // an unrelated edit on the same bag

        Assert.Equal(ItemSlot.Armor, Find(after, 100).Equipped);
        Assert.Equal(ItemSlot.Weapon, Find(after, 101).Equipped);
    }
}
