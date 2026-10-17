using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.Domain.Tests.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Attempts;

public class LoadoutSnapshotTests
{
    const long Account = 5;
    const long Version = 42;
    const string CatalogueVersion = "sha256:abc";
    const long Op1 = 101; // Golden.One, 35 XP: rank 3 (thresholds 0, 10, 30)
    const long Op2 = 102; // Golden.Two, 0 XP: rank 1
    const long Op3 = 103; // Golden.One, never chosen here
    const string Rifle = "item.alpha-rifle";
    const string Vest = "item.beta-vest";
    const string Boots = "item.delta-boots";
    const string Kit = "item.gamma-kit";

    static readonly CatalogueDocument Cat = Golden.Document();

    static List<SnapshotOperativeSource> Roster() =>
    [
        new(Op1, Golden.One, 35, ["choice.one", "choice.two"]),
        new(Op2, Golden.Two, 0, []),
        new(Op3, Golden.One, 12, ["choice.two"]),
    ];

    static List<HeldInstance> Instances() =>
    [
        new(7, Rifle, Op1, ItemSlot.Weapon),
        new(5, Vest, Op1, ItemSlot.Armor),
        new(9, Boots, Op1, null),
        new(11, Rifle, Op2, ItemSlot.Weapon),
        new(20, Rifle, Op3, ItemSlot.Weapon),
        new(30, Vest, null, null),
    ];

    static List<HeldStack> Stacks() =>
    [
        new(Op1, Kit, 2),
        new(Op2, Kit, 1),
        new(Op3, Kit, 4),
        new(null, Kit, 9),
    ];

    static InventoryModel Model(IEnumerable<HeldInstance>? instances = null, IEnumerable<HeldStack>? stacks = null) =>
        new([Op1, Op2, Op3], (instances ?? Instances()).ToList(), (stacks ?? Stacks()).ToList());

    static LoadoutBuild Build(IReadOnlyList<long> order, InventoryModel? model = null, IReadOnlyList<SnapshotOperativeSource>? roster = null) =>
        LoadoutSnapshot.Build(Account, Version, CatalogueVersion, order, roster ?? Roster(), model ?? Model(), Cat);

    static string B(long id) => Base58Id.Format(id);

    [Fact]
    public void LoadoutSnapshot_holds_only_the_chosen_operatives_bags_and_equipment()
    {
        var built = Build([Op1, Op2]);
        Assert.Empty(built.UnknownItems);
        var snapshot = built.Snapshot;

        Assert.Equal(Account, snapshot.AccountId);
        Assert.Equal(Version, snapshot.InventoryVersion);
        Assert.Equal(CatalogueVersion, snapshot.CatalogueVersion);
        Assert.Equal([Op1, Op2], snapshot.Operatives.Select(o => o.OperativeId));

        var first = snapshot.Operatives[0];
        Assert.Equal(35, first.Experience);
        Assert.Equal(3, first.Rank);
        Assert.Equal(["choice.one", "choice.two"], first.Choices);
        Assert.Equal([new SnapshotInstance(5, Vest), new SnapshotInstance(7, Rifle), new SnapshotInstance(9, Boots)], first.Instances);
        Assert.Equal([new SnapshotStack(Kit, 2)], first.Stacks);
        Assert.Equal(new SnapshotEquipment(7, 5, null), first.Equipment);

        var second = snapshot.Operatives[1];
        Assert.Equal(1, second.Rank);
        Assert.Empty(second.Choices);
        Assert.Equal([new SnapshotInstance(11, Rifle)], second.Instances);
        Assert.Equal([new SnapshotStack(Kit, 1)], second.Stacks);
        Assert.Equal(new SnapshotEquipment(11, null, null), second.Equipment);

        // Neither the stash nor an operative that was not chosen is copied.
        var json = snapshot.ToJson();
        Assert.DoesNotContain($"\"{B(20)}\"", json);
        Assert.DoesNotContain($"\"{B(30)}\"", json);
        Assert.DoesNotContain($"\"{B(Op3)}\"", json);
        Assert.DoesNotContain("\"quantity\":9", json);
        Assert.DoesNotContain("\"quantity\":4", json);
        Assert.DoesNotContain("stash", json);
    }

    [Fact]
    public void LoadoutSnapshot_keeps_the_requested_order_and_both_identities()
    {
        var snapshot = Build([Op2, Op1]).Snapshot;

        Assert.Equal([0, 1], snapshot.Operatives.Select(o => o.Slot));
        Assert.Equal([Op2, Op1], snapshot.Operatives.Select(o => o.OperativeId));
        Assert.Equal([Golden.Two, Golden.One], snapshot.Operatives.Select(o => o.DefinitionId));

        var expected = $$$"""{"accountId":"{{{B(Account)}}}","inventoryVersion":42,"catalogueVersion":"sha256:abc","operatives":[{"slot":0,"operativeId":"{{{B(Op2)}}}","definitionId":"{{{Golden.Two}}}","experience":0,"rank":1,"choices":[],"bag":{"instances":[{"itemId":"{{{B(11)}}}","definitionId":"item.alpha-rifle"}],"stacks":[{"definitionId":"item.gamma-kit","quantity":1}]},"equipment":{"weapon":"{{{B(11)}}}","armor":null,"utility":null}},{"slot":1,"operativeId":"{{{B(Op1)}}}","definitionId":"{{{Golden.One}}}","experience":35,"rank":3,"choices":["choice.one","choice.two"],"bag":{"instances":[{"itemId":"{{{B(5)}}}","definitionId":"item.beta-vest"},{"itemId":"{{{B(7)}}}","definitionId":"item.alpha-rifle"},{"itemId":"{{{B(9)}}}","definitionId":"item.delta-boots"}],"stacks":[{"definitionId":"item.gamma-kit","quantity":2}]},"equipment":{"weapon":"{{{B(7)}}}","armor":"{{{B(5)}}}","utility":null}}]}""";
        Assert.Equal(expected, snapshot.ToJson());

        // Another order is another snapshot.
        Assert.NotEqual(snapshot.ToJson(), Build([Op1, Op2]).Snapshot.ToJson());
    }

    [Fact]
    public void LoadoutSnapshot_json_is_deterministic()
    {
        var reference = Build([Op2, Op1]).Snapshot.ToJson();

        // The row order the database happens to return changes nothing.
        var shuffledInstances = Instances().AsEnumerable().Reverse().ToList();
        var shuffledStacks = Stacks().AsEnumerable().Reverse().ToList();
        var shuffledRoster = Roster().AsEnumerable().Reverse().ToList();
        Assert.Equal(reference, Build([Op2, Op1], Model(shuffledInstances, shuffledStacks), shuffledRoster).Snapshot.ToJson());

        // Two stacks of different definitions sort by definition id, ordinally.
        var withAmmo = Build([Op1], Model(stacks: [new(Op1, Kit, 2), new(Op1, "item.ammo", 3)])).Snapshot.ToJson();
        Assert.Contains("\"stacks\":[{\"definitionId\":\"item.ammo\",\"quantity\":3},{\"definitionId\":\"item.gamma-kit\",\"quantity\":2}]", withAmmo);

        // Calling it twice gives the same text.
        var snapshot = Build([Op2, Op1]).Snapshot;
        Assert.Equal(snapshot.ToJson(), snapshot.ToJson());
    }

    [Fact]
    public void LoadoutSnapshot_reports_unknown_item_definitions()
    {
        var instances = Instances();
        instances.Add(new(40, "item.retired", Op1, null));
        instances.Add(new(41, "item.retired-blade", Op1, ItemSlot.Utility));
        instances.Add(new(42, "item.lost", Op3, null)); // not chosen: not reported
        instances.Add(new(43, "item.lost", null, null)); // stash: not reported
        var stacks = Stacks();
        stacks.Add(new(Op1, "item.old-kit", 2));
        stacks.Add(new(Op1, "item.retired", 1));

        var built = Build([Op2, Op1], Model(instances, stacks));

        Assert.Equal(
            [new UnknownItemDefinition(1, "item.old-kit"), new UnknownItemDefinition(1, "item.retired"), new UnknownItemDefinition(1, "item.retired-blade")],
            built.UnknownItems);
        // The snapshot still lists them, so the caller can decide; admission refuses (422 UnknownItem).
        Assert.Contains(new SnapshotInstance(40, "item.retired"), built.Snapshot.Operatives[1].Instances);
        Assert.Equal(41, built.Snapshot.Operatives[1].Equipment.Utility);
    }

    public static TheoryData<string> BadBuilds =>
    [
        "empty order", "duplicate in the order", "operative not in the roster", "roster lists an operative twice", "account id zero",
        "negative inventory version", "inventory version zero", "empty catalogue version", "two items in one slot",
        "equipped in the stash", "equipped as None", "two stacks of one definition", "non-positive stack",
    ];

    [Theory]
    [MemberData(nameof(BadBuilds))]
    public void LoadoutSnapshot_refuses_malformed_input(string label)
    {
        Action call = label switch
        {
            "empty order" => () => Build([]),
            "duplicate in the order" => () => Build([Op1, Op1]),
            "operative not in the roster" => () => Build([Op1, 999]),
            "roster lists an operative twice" => () => Build([Op1], roster: [.. Roster(), new(Op1, Golden.One, 0, [])]),
            "account id zero" => () => LoadoutSnapshot.Build(0, Version, CatalogueVersion, [Op1], Roster(), Model(), Cat),
            "negative inventory version" => () => LoadoutSnapshot.Build(Account, -1, CatalogueVersion, [Op1], Roster(), Model(), Cat),
            // 0007 checks attempt_participants.inventory_version >= 1, and inventories.version starts at 1.
            "inventory version zero" => () => LoadoutSnapshot.Build(Account, 0, CatalogueVersion, [Op1], Roster(), Model(), Cat),
            "empty catalogue version" => () => LoadoutSnapshot.Build(Account, Version, "", [Op1], Roster(), Model(), Cat),
            "two items in one slot" => () => Build([Op1], Model([.. Instances(), new(50, Rifle, Op1, ItemSlot.Weapon)])),
            "equipped in the stash" => () => Build([Op1], Model([.. Instances(), new(50, Rifle, null, ItemSlot.Weapon)])),
            "equipped as None" => () => Build([Op1], Model([.. Instances(), new(50, Rifle, Op1, ItemSlot.None)])),
            "two stacks of one definition" => () => Build([Op1], Model(stacks: [new(Op1, Kit, 2), new(Op1, Kit, 1)])),
            "non-positive stack" => () => Build([Op1], Model(stacks: [new(Op1, Kit, 0)])),
            _ => throw new InvalidOperationException(label),
        };
        Assert.ThrowsAny<ArgumentException>(call);
    }
}
